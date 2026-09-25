using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundMeeter.Audio;
using SoundMeeter.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;

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

            // Каталог устройств захвата (микрофоны)
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                _catalog.Add(new DeviceInfo(device.ID, device.FriendlyName, true));
            }

            // Каталог устройств воспроизведения (для loopback-входов и выходных шин)
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _catalog.Add(new DeviceInfo(device.ID, device.FriendlyName, false));
            }

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

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;

            OpenAllBusesUnlocked();
            ApplyRoutingUnlocked();
            IsRunning = true;
        }
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
                try { busOut.Out.Stop(); } catch { }
                busOut.Out.Dispose();
            }
            _openBuses.Clear();
        }
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Bus '{bus.Name}' failed to open: {ex.Message}");
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
                    var tap = new BusTap(source.OpenCursor(), input, _soloState);
                    busOut.Dsp.AddInput(tap);
                    _taps[key] = tap;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Route {input.Name} -> {busOut.Model.Name} failed: {ex.Message}");
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

        StopOrphanSourcesUnlocked();
    }

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
            Debug.WriteLine($"Source '{input.Name}' failed: {ex.Message}");
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
        VolumeDb = source.VolumeDb,
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