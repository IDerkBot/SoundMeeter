using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Collections.ObjectModel;

namespace SoundMeeter.Services;

/// <summary>
/// Аудиодвижок в стиле VoiceMeeter: пользователь создаёт стрипы (входы/выходы),
/// назначает им устройства из каталога. Роутинг — матрица вход×шина (bus.Id),
/// применяется на лету.
/// </summary>
public sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly object _gate = new();
    private readonly SoloState _soloState = new();
    private readonly List<DeviceInfo> _catalog = new();
    private readonly HashSet<string> _removedDeviceIds = new();
    private readonly ILogger _logger = AppLog.For<WasapiAudioEngine>();

    public List<InputChannelModel> InputsInternal { get; } = new();
    public List<OutputBusModel> BusesInternal { get; } = new();
    public IReadOnlyList<InputChannelModel> Inputs => InputsInternal;
    public IReadOnlyList<OutputBusModel> Buses => BusesInternal;
    public IReadOnlyList<DeviceInfo> Catalog => new ReadOnlyCollection<DeviceInfo>(_catalog);

    public bool IsRunning { get; private set; }

    /// <summary>Настройки MIDI-микшера (устройство + привязки контроллеров).</summary>
    public MidiSettings Midi { get; set; } = new();

    public event Action? ChannelsChanged;
    public event Action? StateChanged;

    // Открытые во время работы объекты
    private readonly Dictionary<string, BusOutput> _openBuses = new();          // по bus.Id
    private readonly Dictionary<string, InputSource> _sources = new();          // по input.Id
    private readonly Dictionary<(string InputId, string BusId), BusTap> _taps = new();

    private sealed class BusOutput
    {
        public required OutputBusModel Model;
        public required BusDsp Dsp;
        public required WasapiOut Out;
        public bool Active;
    }

    public void RefreshDevices()
    {
        bool wasRunning = IsRunning;
        Stop(); // захватит снапшот и остановит

        lock (_gate)
        {
            _catalog.Clear();

            using var enumerator = new MMDeviceEnumerator();

            var devices = new List<DeviceInfo>();

            // Каталог устройств захвата (микрофоны)
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add(new DeviceInfo(device.ID, device.FriendlyName, true));
            }

            // Каталог устройств воспроизведения (для loopback-входов и выходных шин)
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new DeviceInfo(device.ID, device.FriendlyName, false));
            }

            // Связываем половинки виртуальных кабелей: приложения уходят в его выход,
            // а звук приходит в стрип входа. Без этой связи нельзя понять, куда
            // перенаправлять приложение, снятые с входа кабеля.
            _catalog.AddRange(CablePairing.Link(devices));

            // Проверяем доступность существующих стрипов
            var catalogIds = new HashSet<string>(_catalog.Select(d => d.DeviceId));
            foreach (var input in InputsInternal)
            {
                input.IsAvailable = !string.IsNullOrEmpty(input.DeviceId) && catalogIds.Contains(input.DeviceId);
            }
            foreach (var bus in BusesInternal)
            {
                bus.IsAvailable = !string.IsNullOrEmpty(bus.DeviceId) && catalogIds.Contains(bus.DeviceId);
            }

            // Автоматически берём в работу устройства, у которых ещё нет стрипа
            // (микрофоны → входы, устройства воспроизведения → выходные шины).
            AdoptDevicesUnlocked();

            // Стрип «SPK CABLE-x Output» при уже снятом входе того же кабеля —
            // дубликат: приложения роутятся прямо в выход кабеля. Убираем.
            DropRedundantCableLoopbacksUnlocked();

            // Ручной выбор цели приложений мог устареть: устройство отключили или
            // сменилось направление. Тогда возвращаем автоматическое определение.
            foreach (var input in InputsInternal)
            {
                if (string.IsNullOrEmpty(input.AppTargetDeviceId)) continue;

                var target = _catalog.FirstOrDefault(d => SameDevice(d.DeviceId, input.AppTargetDeviceId));
                if (target == null || target.IsMicrophone) input.AppTargetDeviceId = "";
            }
        }

        ChannelsChanged?.Invoke();
        if (wasRunning) Start();
    }

    private void AdoptDevicesUnlocked()
    {
        foreach (var device in _catalog)
        {
            if (_removedDeviceIds.Contains(device.DeviceId)) continue;

            if (device.IsMicrophone)
            {
                if (InputsInternal.All(i => i.DeviceId != device.DeviceId))
                {
                    InputsInternal.Add(new InputChannelModel
                    {
                        Name = $"MIC {device.Name}",
                        IsMicrophone = true,
                        DeviceId = device.DeviceId,
                        IsAvailable = true
                    });
                }
            }
            else
            {
                if (BusesInternal.All(b => b.DeviceId != device.DeviceId))
                {
                    BusesInternal.Add(new OutputBusModel
                    {
                        Name = device.Name,
                        DeviceId = device.DeviceId,
                        IsAvailable = true
                    });
                }
            }
        }

        EnsureRoutingTableUnlocked();
    }

    /// <summary>
    /// Убирает входные стрипы, снимающие loopback'ом выход виртуального кабеля,
    /// если половинка того же кабеля со стороны захвата уже снята своим стрипом.
    /// Такой стрип только дублирует канал: приложения играют прямо в выход кабеля,
    /// а маршрут «выход кабеля → выход кабеля» к тому же даёт петлю обратной связи.
    /// </summary>
    private void DropRedundantCableLoopbacksUnlocked()
    {
        foreach (var input in InputsInternal.ToList())
        {
            if (input.IsMicrophone || string.IsNullOrEmpty(input.DeviceId)) continue;

            var cable = _catalog.FirstOrDefault(d => !d.IsMicrophone && d.IsVirtualCable &&
                                                     SameDevice(d.DeviceId, input.DeviceId));
            if (cable?.CablePeerId is not { } peerId) continue;

            bool peerCaptured = InputsInternal.Any(other =>
                !ReferenceEquals(other, input) && SameDevice(other.DeviceId, peerId));
            if (!peerCaptured) continue;

            DetachInputUnlocked(input);
            InputsInternal.Remove(input);

            _logger.LogInformation(
                "Вход «{Strip}» (loopback выхода кабеля «{Cable}») удалён: приложения " +
                "направляются в связанный выход кабеля, звук приходит в стрип его входа",
                input.Name, cable.Name);
        }

        _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
        EnsureRoutingTableUnlocked();
    }

    /// <summary>Отключает источник и все тапы стрипа (сам стрип остаётся в списке).</summary>
    private void DetachInputUnlocked(InputChannelModel input)
    {
        if (_sources.Remove(input.Id, out var source)) source.Dispose();

        foreach (var key in _taps.Keys.Where(k => k.InputId == input.Id).ToList())
        {
            if (_openBuses.TryGetValue(key.BusId, out var busOut))
                busOut.Dsp.RemoveInput(_taps[key]);
            _taps.Remove(key);
        }
    }

    private static bool SameDevice(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;

            OpenAllBusesUnlocked();
            ApplyRoutingUnlocked();
            IsRunning = true;
        }
        _logger.LogInformation("Engine started: {Inputs} inputs, {Buses} buses, {Taps} active routes",
            InputsInternal.Count, BusesInternal.Count, _taps.Count);
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;

            foreach (var key in _taps.Keys.ToList())
            {
                if (_openBuses.TryGetValue(key.BusId, out var busOut))
                    busOut.Dsp.RemoveInput(_taps[key]);
            }
            _taps.Clear();

            foreach (var source in _sources.Values) source.Dispose();
            _sources.Clear();

            foreach (var busOut in _openBuses.Values)
            {
                try { busOut.Out.Stop(); } catch (Exception ex) { _logger.LogDebug(ex, "Stop of bus «{Bus}» failed: {Message}", busOut.Model.Name, ex.Message); }
                busOut.Out.Dispose();
            }
            _openBuses.Clear();
        }
        _logger.LogInformation("Engine stopped");
        StateChanged?.Invoke();
    }

    public void SetRoute(string inputId, string busId, bool enabled)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;

            if (!input.BusRouting.TryGetValue(busId, out var route))
            {
                route = new BusRouting();
                input.BusRouting[busId] = route;
            }
            route.Enabled = enabled;

            if (IsRunning) ApplyRoutingUnlocked();
        }
    }

    /// <summary>
    /// Индивидуальная посылка входа в шину, дБ. Значение читается тапом на каждом
    /// пакете, поэтому применение мгновенное: пересоздавать аудиопоток не нужно.
    /// </summary>
    public void SetRouteGain(string inputId, string busId, float gainDb)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;

            if (!input.BusRouting.TryGetValue(busId, out var route))
            {
                route = new BusRouting();
                input.BusRouting[busId] = route;
            }

            if (!float.IsFinite(gainDb)) gainDb = 0f;
            route.GainDb = Math.Clamp(gainDb, BusTap.MinGainDb, BusTap.MaxGainDb);
        }
    }

    public void SetInputSolo(string inputId, bool value)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;
            input.IsSolo = value;
            _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
        }
    }

    public void SetOutputSolo(string busId, bool value)
    {
        lock (_gate)
        {
            var bus = BusesInternal.FirstOrDefault(b => b.Id == busId);
            if (bus == null) return;
            bus.IsSolo = value;
            _soloState.AnyOutputSolo = BusesInternal.Any(b => b.IsSolo);
        }
    }

    public void AddInput()
    {
        lock (_gate)
        {
            var input = new InputChannelModel
            {
                Name = "Unassigned input",
                IsAvailable = false
            };
            InputsInternal.Add(input);
            EnsureRoutingTableUnlocked();
            _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
        }
        ChannelsChanged?.Invoke();
    }

    public void RemoveInput(string inputId)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;

            if (_sources.TryGetValue(inputId, out var source))
            {
                source.Dispose();
                _sources.Remove(inputId);
            }

            foreach (var key in _taps.Keys.Where(k => k.InputId == inputId).ToList())
            {
                if (_openBuses.TryGetValue(key.BusId, out var busOut))
                    busOut.Dsp.RemoveInput(_taps[key]);
                _taps.Remove(key);
            }

            if (!string.IsNullOrEmpty(input.DeviceId))
                _removedDeviceIds.Add(input.DeviceId);

            InputsInternal.Remove(input);
            _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
        }
        ChannelsChanged?.Invoke();
    }

    public void AddBus()
    {
        lock (_gate)
        {
            var bus = new OutputBusModel
            {
                Name = "Unassigned",
                IsAvailable = false
            };
            BusesInternal.Add(bus);
            EnsureRoutingTableUnlocked();
            _soloState.AnyOutputSolo = BusesInternal.Any(b => b.IsSolo);
        }
        ChannelsChanged?.Invoke();
    }

    public void MoveInput(int fromIndex, int toIndex)
    {
        lock (_gate)
        {
            if (!IsValidMove(fromIndex, toIndex, InputsInternal.Count)) return;
            Move(InputsInternal, fromIndex, toIndex);
        }
        // Каналы те же самые, меняется только их порядок в UI, поэтому представления
        // пересобираются, а аудиопоток остаётся нетронутым.
        ChannelsChanged?.Invoke();
    }

    public void MoveBus(int fromIndex, int toIndex)
    {
        lock (_gate)
        {
            if (!IsValidMove(fromIndex, toIndex, BusesInternal.Count)) return;
            Move(BusesInternal, fromIndex, toIndex);
        }
        ChannelsChanged?.Invoke();
    }

    private static bool IsValidMove(int fromIndex, int toIndex, int count) =>
        fromIndex >= 0 && fromIndex < count && toIndex >= 0 && toIndex < count && fromIndex != toIndex;

    private static void Move<T>(List<T> list, int fromIndex, int toIndex)
    {
        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, item);
    }

    public void RemoveBus(string busId)
    {
        lock (_gate)
        {
            var bus = BusesInternal.FirstOrDefault(b => b.Id == busId);
            if (bus == null) return;

            if (_openBuses.TryGetValue(busId, out var busOut))
            {
                foreach (var key in _taps.Keys.Where(k => k.BusId == busId).ToList())
                {
                    busOut.Dsp.RemoveInput(_taps[key]);
                    _taps.Remove(key);
                }
                try { busOut.Out.Stop(); } catch { }
                busOut.Out.Dispose();
                _openBuses.Remove(busId);
            }

            BusesInternal.Remove(bus);
            if (!string.IsNullOrEmpty(bus.DeviceId))
                _removedDeviceIds.Add(bus.DeviceId);
            foreach (var input in InputsInternal)
                input.BusRouting.Remove(busId);

            _soloState.AnyOutputSolo = BusesInternal.Any(b => b.IsSolo);
        }
        ChannelsChanged?.Invoke();
    }

    public void SetInputSource(string inputId, string? deviceId)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;

            var device = _catalog.FirstOrDefault(d => d.DeviceId == deviceId);
            if (deviceId != null && device == null) return; // устройство не в каталоге

            // Выход виртуального кабеля источником входа не становится: приложения
            // направляются прямо в него, а звук кабеля приходит в стрип его входа.
            // Loopback этого выхода дал бы второй канал того же самого звука.
            if (device is { IsVirtualCable: true, IsMicrophone: false })
            {
                _logger.LogInformation(
                    "Выход кабеля «{Device}» нельзя назначить входу: приложения роутятся " +
                    "в него напрямую, звук снимается стрипом входа кабеля", device.Name);
                return;
            }

            // Одно устройство — на один стрип. Иначе два стрипа на одном источнике
            // дают дублирование сигнала (глубокий клиппинг → «шум»).
            if (deviceId != null && deviceId != input.DeviceId)
            {
                var taken = InputsInternal.Any(i => i.Id != inputId && i.DeviceId == deviceId);
                if (taken) return;
            }

            input.DeviceId = deviceId ?? "";
            input.IsMicrophone = device?.IsMicrophone ?? false;
            input.Name = deviceId == null
                ? "Unassigned input"
                : (device!.IsMicrophone ? $"MIC {device.Name}" : $"SPK {device.Name}");
            input.IsAvailable = deviceId != null;

            if (deviceId != null)
                _removedDeviceIds.Remove(deviceId);

            if (IsRunning)
            {
                if (_sources.TryGetValue(inputId, out var src))
                {
                    src.Dispose();
                    _sources.Remove(inputId);
                }
                foreach (var key in _taps.Keys.Where(k => k.InputId == inputId).ToList())
                {
                    if (_openBuses.TryGetValue(key.BusId, out var busOut))
                        busOut.Dsp.RemoveInput(_taps[key]);
                    _taps.Remove(key);
                }
                if (!string.IsNullOrEmpty(input.DeviceId))
                    ApplyRoutingUnlocked();
            }
        }
        ChannelsChanged?.Invoke();
    }

    public void SetInputAppTarget(string inputId, string? deviceId)
    {
        lock (_gate)
        {
            var input = InputsInternal.FirstOrDefault(i => i.Id == inputId);
            if (input == null) return;

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                input.AppTargetDeviceId = "";
            }
            else
            {
                // Приложения играют только в устройства воспроизведения: микрофон
                // целью быть не может — звук оттуда не придёт.
                var device = _catalog.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device == null || device.IsMicrophone) return;

                input.AppTargetDeviceId = deviceId;
            }
        }

        // Аудиопоток не меняется — меняется только список приложений стрипа,
        // поэтому пересобираем представления каналов, а не маршруты.
        ChannelsChanged?.Invoke();
    }

    public void SetBusSource(string busId, string? deviceId)
    {
        lock (_gate)
        {
            var bus = BusesInternal.FirstOrDefault(b => b.Id == busId);
            if (bus == null) return;

            var device = _catalog.FirstOrDefault(d => d.DeviceId == deviceId);
            if (deviceId != null && device == null) return;

            var oldDeviceId = bus.DeviceId;

            // Одно устройство вывода — на одну шину (иначе дублирование/клиппинг).
            if (deviceId != null && deviceId != oldDeviceId)
            {
                var taken = BusesInternal.Any(b => b.Id != busId && b.DeviceId == deviceId);
                if (taken) return;
            }

            bus.DeviceId = deviceId ?? "";
            bus.Name = deviceId == null ? "Unassigned" : device!.Name;
            bus.IsAvailable = deviceId != null;

            if (deviceId != null)
                _removedDeviceIds.Remove(deviceId);

            if (IsRunning)
            {
                // закрываем старый вывод шины
                if (_openBuses.TryGetValue(busId, out var oldOut))
                {
                    foreach (var key in _taps.Keys.Where(k => k.BusId == busId).ToList())
                    {
                        oldOut.Dsp.RemoveInput(_taps[key]);
                        _taps.Remove(key);
                    }
                    try { oldOut.Out.Stop(); } catch { }
                    oldOut.Out.Dispose();
                    _openBuses.Remove(busId);
                }

                // открываем новый, если назначен
                if (!string.IsNullOrEmpty(bus.DeviceId))
                {
                    OpenBusUnlocked(bus);
                }
                ApplyRoutingUnlocked();
            }
        }
        ChannelsChanged?.Invoke();
    }

    public void EnsureDefaultStrips()
    {
        lock (_gate)
        {
            if (InputsInternal.Count == 0)
            {
                InputsInternal.Add(new InputChannelModel { Name = "Unassigned input", IsAvailable = false });
                InputsInternal.Add(new InputChannelModel { Name = "Unassigned input", IsAvailable = false });
            }
            if (BusesInternal.Count == 0)
            {
                BusesInternal.Add(new OutputBusModel { Name = "Unassigned", IsAvailable = false });
                BusesInternal.Add(new OutputBusModel { Name = "Unassigned", IsAvailable = false });
            }
            EnsureRoutingTableUnlocked();
        }
    }

    public AppSettings CreateSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new AppSettings { EngineWasRunning = IsRunning };
            foreach (var i in InputsInternal) snapshot.Inputs.Add(CloneInput(i));
            foreach (var b in BusesInternal) snapshot.Outputs.Add(CloneBus(b));
            snapshot.RemovedDeviceIds = new List<string>(_removedDeviceIds);
            snapshot.Midi = Midi.Clone();
            return snapshot;
        }
    }

    public void ApplyPreset(AppSettings preset)
    {
        lock (_gate)
        {
            // Восстанавливаем список «скрытых» устройств до повторного осмотра
            // каталога, чтобы AdoptDevicesUnlocked не создал им стрипы заново.
            _removedDeviceIds.Clear();
            if (preset.RemovedDeviceIds != null)
                foreach (var id in preset.RemovedDeviceIds) _removedDeviceIds.Add(id);

            Midi = preset.Midi?.Clone() ?? new MidiSettings();

            // Скрытые устройства не должны восстанавливаться даже из сохранённого
            // пресета: стрип такого устройства удаляется пользователем, а во время
            // старого сохранения он мог ещё числиться в Inputs/Outputs.
            InputsInternal.Clear();
            var usedInputDevices = new HashSet<string>();
            foreach (var p in preset.Inputs)
            {
                // Гарантируем, что одно устройство не захватывается двумя входами.
                if (!string.IsNullOrEmpty(p.DeviceId) && !usedInputDevices.Add(p.DeviceId))
                    continue;
                // Не восстанавливаем стрип устройства, которое пользователь скрыл.
                if (!string.IsNullOrEmpty(p.DeviceId) && _removedDeviceIds.Contains(p.DeviceId))
                    continue;
                InputsInternal.Add(CloneInput(p));
            }

            BusesInternal.Clear();
            var usedBusDevices = new HashSet<string>();
            foreach (var p in preset.Outputs)
            {
                // Гарантируем, что одно устройство вывода не отдаёт двум шинам.
                if (!string.IsNullOrEmpty(p.DeviceId) && !usedBusDevices.Add(p.DeviceId))
                    continue;
                // Не восстанавливаем стрип устройства, которое пользователь скрыл.
                if (!string.IsNullOrEmpty(p.DeviceId) && _removedDeviceIds.Contains(p.DeviceId))
                    continue;
                BusesInternal.Add(CloneBus(p));
            }

            // восстанавливаем доступность по текущему каталогу
            var catIds = new HashSet<string>(_catalog.Select(d => d.DeviceId));
            foreach (var input in InputsInternal)
                input.IsAvailable = !string.IsNullOrEmpty(input.DeviceId) && catIds.Contains(input.DeviceId);
            foreach (var bus in BusesInternal)
                bus.IsAvailable = !string.IsNullOrEmpty(bus.DeviceId) && catIds.Contains(bus.DeviceId);

            _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
            _soloState.AnyOutputSolo = BusesInternal.Any(b => b.IsSolo);

            EnsureRoutingTableUnlocked();
        }
        ChannelsChanged?.Invoke();
    }

    public void EnsureRoutingTable()
    {
        lock (_gate) EnsureRoutingTableUnlocked();
    }

    public void Dispose() => Stop();

    private void OpenAllBusesUnlocked()
    {
        _openBuses.Clear();

        foreach (var bus in BusesInternal)
        {
            OpenBusUnlocked(bus);
        }
    }

    private void OpenBusUnlocked(OutputBusModel bus)
    {
        if (string.IsNullOrEmpty(bus.DeviceId))
        {
            bus.IsAvailable = false;
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(bus.DeviceId);
            if (device == null) throw new InvalidOperationException($"Device {bus.DeviceId} not found");

            var dsp = new BusDsp(bus, _soloState);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(new SampleToWaveProvider(dsp));
            output.Play();

            var busOutput = new BusOutput { Model = bus, Dsp = dsp, Out = output, Active = true };
            _openBuses[bus.Id] = busOutput;
            bus.IsAvailable = true;
            _logger.LogInformation("Bus «{Bus}» opened: device={Device}, «{DeviceName}», формат {Format}",
                bus.Name, bus.DeviceId, device.FriendlyName, dsp.WaveFormat);
        }
        catch (Exception ex)
        {
            // HRESULT обязателен в записи: по тексту исключения нельзя понять,
            // это запрет эксклюзивного режима, занятость устройства или отказ
            // драйвера — а разбираться приходится уже пользователю.
            _logger.LogError(ex, "Bus «{Bus}» (device={Device}) failed to open, HRESULT=0x{HResult:X8}: {Message}",
                bus.Name, bus.DeviceId, ex.HResult, ex.Message);
            bus.IsAvailable = false;
        }
    }

    private void ApplyRoutingUnlocked()
    {
        var desired = new HashSet<(string InputId, string BusId)>();

        foreach (var input in InputsInternal)
        {
            if (string.IsNullOrEmpty(input.DeviceId)) continue; // нет источника — не маршрутизируем

            foreach (var pair in input.BusRouting)
            {
                if (!pair.Value.Enabled) continue;
                if (!_openBuses.TryGetValue(pair.Key, out var busOut) || !busOut.Active) continue;

                var key = (input.Id, busOut.Model.Id);
                desired.Add(key);
                if (_taps.ContainsKey(key)) continue;

                var source = GetOrCreateSourceUnlocked(input);
                if (source == null) continue;

                try
                {
                    // Тап получает живой объект маршрута: посылка GainDb читается
                    // из него на каждом пакете, поэтому правка уровня в UI слышна
                    // без пересоздания тапа (SM-A02).
                    var tap = new BusTap(source.OpenCursor(), input, pair.Value, _soloState);
                    busOut.Dsp.AddInput(tap);
                    _taps[key] = tap;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Маршрут {Input} -> {Bus} не построен: {Message}",
                        input.Name, busOut.Model.Name, ex.Message);
                }
            }
        }

        var stale = _taps.Keys.Where(k => !desired.Contains(k)).ToList();
        foreach (var key in stale)
        {
            if (_openBuses.TryGetValue(key.BusId, out var busOut))
                busOut.Dsp.RemoveInput(_taps[key]);
            _taps.Remove(key);
        }

        if (stale.Count > 0)
        {
            // Сброс маршрутизации виден только здесь: без записи в журнал
            // «пропал звук на стрипе» нечем объяснить.
            _logger.LogInformation("Routing rebuilt: removed {Removed}, active now {Active}",
                string.Join(", ", stale.Select(k => $"{NameOf(k.InputId)} -> {NameOf(k.BusId)}")),
                _taps.Count);
        }

        StopOrphanSourcesUnlocked();
    }

    private string NameOf(string stripId) =>
        InputsInternal.FirstOrDefault(i => i.Id == stripId)?.Name ?? stripId;

    private InputSource? GetOrCreateSourceUnlocked(InputChannelModel input)
    {
        if (_sources.TryGetValue(input.Id, out var existing)) return existing;

        var source = new InputSource(input);
        try
        {
            source.Start();
            _sources[input.Id] = source;
            return source;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source «{Strip}» (device={Device}) failed, HRESULT=0x{HResult:X8}: {Message}",
                input.Name, input.DeviceId, ex.HResult, ex.Message);
            source.Dispose();
            return null;
        }
    }

    private void StopOrphanSourcesUnlocked()
    {
        foreach (var inputId in _sources.Keys.ToList())
        {
            if (_taps.Keys.All(k => k.InputId != inputId))
            {
                _sources[inputId].Dispose();
                _sources.Remove(inputId);
            }
        }
    }

    private void ApplyPresetUnlocked(AppSettings preset)
    {
        foreach (var input in InputsInternal)
        {
            var saved = preset.Inputs.FirstOrDefault(x => x.Id == input.Id);
            if (saved != null)
            {
                input.VolumeDb = saved.VolumeDb;
                input.IsMuted = saved.IsMuted;
                input.IsMono = saved.IsMono;
                input.IsSolo = saved.IsSolo;
                input.BusRouting = CloneRouting(saved.BusRouting);
            }
        }

        foreach (var bus in BusesInternal)
        {
            var saved = preset.Outputs.FirstOrDefault(x => x.Id == bus.Id);
            if (saved != null)
            {
                bus.VolumeDb = saved.VolumeDb;
                bus.IsMuted = saved.IsMuted;
                bus.IsMono = saved.IsMono;
                bus.IsSolo = saved.IsSolo;
            }
        }

        _soloState.AnyInputSolo = InputsInternal.Any(i => i.IsSolo);
        _soloState.AnyOutputSolo = BusesInternal.Any(b => b.IsSolo);

        EnsureRoutingTableUnlocked();
    }

    private void EnsureRoutingTableUnlocked()
    {
        var validBusIds = new HashSet<string>(BusesInternal.Select(b => b.Id));

        foreach (var input in InputsInternal)
        {
            foreach (var busId in input.BusRouting.Keys.Where(k => !validBusIds.Contains(k)).ToList())
                input.BusRouting.Remove(busId);

            foreach (var bus in BusesInternal)
            {
                if (!input.BusRouting.ContainsKey(bus.Id))
                    input.BusRouting[bus.Id] = new BusRouting();
            }
        }
    }

    private static InputChannelModel CloneInput(InputChannelModel source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ChannelName = source.ChannelName,
        IsMicrophone = source.IsMicrophone,
        DeviceId = source.DeviceId,
        AppTargetDeviceId = source.AppTargetDeviceId,
        VolumeDb = source.VolumeDb,
        GainDb = source.GainDb,
        IsMuted = source.IsMuted,
        IsMono = source.IsMono,
        IsSolo = source.IsSolo,
        DenoiserEnabled = source.DenoiserEnabled,
        DenoiserNoiseRemover = source.DenoiserNoiseRemover,
        DenoiserDryWet = source.DenoiserDryWet,
        DenoiserFormantLowDb = source.DenoiserFormantLowDb,
        DenoiserFormantMidDb = source.DenoiserFormantMidDb,
        DenoiserFormantHighDb = source.DenoiserFormantHighDb,
        DenoiserFormantGroupDb = source.DenoiserFormantGroupDb,
        BusRouting = CloneRouting(source.BusRouting)
    };

    private static OutputBusModel CloneBus(OutputBusModel source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ChannelName = source.ChannelName,
        DeviceId = source.DeviceId,
        VolumeDb = source.VolumeDb,
        IsMuted = source.IsMuted,
        IsMono = source.IsMono,
        IsSolo = source.IsSolo,
        IsAvailable = source.IsAvailable
    };

    private static Dictionary<string, BusRouting> CloneRouting(Dictionary<string, BusRouting> source)
    {
        var copy = new Dictionary<string, BusRouting>(source.Count);
        foreach (var pair in source)
            copy[pair.Key] = new BusRouting { Enabled = pair.Value.Enabled, GainDb = pair.Value.GainDb };
        return copy;
    }
}