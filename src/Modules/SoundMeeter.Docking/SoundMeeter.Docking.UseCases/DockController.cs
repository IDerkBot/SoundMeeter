using Microsoft.Extensions.Logging;
using SoundMeeter.Audio.Contract;
using SoundMeeter.Docking.Contract;
using SoundMeeter.Docking.Entities;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Infrastructure.Contract;
using SoundMeeter.Framework.Contract;

namespace SoundMeeter.Docking.UseCases;

/// <summary>
/// Оркестрация док-панели OBS: сервер, снимок состояния для панели и применение
/// её команд к стрипам микшера.
///
/// Раньше это был <c>DockViewModel</c> — класс, унаследованный от
/// <c>LocalizedViewModel</c>, в котором не было ни одного наблюдаемого
/// свойства. Наследование ViewModel'а было лишним и обходилось дорого: ядро
/// (<c>SoundMeeter.Core.UseCases</c>) брало док в конструктор и оттуда знало о
/// ViewModel-модуле. Если бы ViewModel переехал в
/// <c>SoundMeeter.Docking.Presentation</c>, получился бы цикл
/// Core → Presentation → UseCases → Presentation.
///
/// Здесь его нет: это обычный класс без UI-наследования, и ядро может зависеть от
/// него, не завися от представления. Окно настроек по-прежнему получает
/// состояние и команды — но через <see cref="IObsDockInstaller"/> и сервер, а
/// не через ViewModel модуля.
///
/// Наследует IDisposable напрямую: освобождение здесь не навешивает ничего на
/// UI, поэтому подписка на статическое событие локализации не нужна и утечки
/// подписчиков (H-10) на этом пути не возникает.
/// </summary>
public sealed class DockController : IDisposable
{
    private readonly IObsDockServer _dock;
    private readonly IObsDockInstaller _installer;
    private readonly ISettingsService _settingsService;
    private readonly IStripProvider _strips;
    private readonly IAudioEngine _engine;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger _logger = AppLog.For<DockController>();
    private readonly Action<int> _onClientsChanged;
    private bool _disposed;
    private string _dockError = "";

    /// <summary>Сервер док-панели. Пока панель не подключена, состояние не собирается вовсе.</summary>
    public IObsDockServer DockServer => _dock;

    /// <summary>Живые настройки дока (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public ObsDockSettings DockSettings => _settingsService.Settings.ObsDock;

    /// <summary>Входные стрипы (для окна настроек).</summary>
    public IReadOnlyList<IInputStrip> Inputs => _strips.Inputs;

    /// <summary>Выходные стрипы (для окна настроек).</summary>
    public IReadOnlyList<IOutputStrip> Buses => _strips.Buses;

    /// <summary>URL панели: фактический порт сервера, иначе порт из настроек.</summary>
    public string DockUrl => _dock.IsRunning
        ? _dock.Url
        : $"http://127.0.0.1:{DockSettings.EffectivePort}/";

    public bool IsDockRunning => _dock.IsRunning;

    /// <summary>Сколько панелей подключено к серверу (для индикатора в окне настроек).</summary>
    public int DockClients => _dock.ClientCount;

    /// <summary>Ошибка последней попытки поднять сервер (пусто — всё в порядке).</summary>
    public string DockError => _dockError;

    /// <summary>Изменилось состояние сервера/подключений — окно настроек обновит надписи.</summary>
    public event Action? DockStatusChanged;

    /// <summary>Док уже прописан в конфигурации OBS?</summary>
    public bool IsDockInstalledInObs => _installer.IsDockInstalled();

    /// <summary>
    /// OBS сейчас запущен — тогда его user.ini править нельзя: работающий OBS
    /// переписывает файл целиком при выходе.
    /// </summary>
    public bool IsObsRunning => _installer.IsObsRunning;

    public DockController(
        IObsDockServer dock,
        IObsDockInstaller installer,
        ISettingsService settingsService,
        IStripProvider strips,
        IAudioEngine engine,
        IDispatcherService dispatcherService)
    {
        _dock = dock;
        _installer = installer;
        _settingsService = settingsService;
        _strips = strips;
        _engine = engine;
        _dispatcherService = dispatcherService;

        _dock.CommandReceived += OnDockCommand;

        // Обработчик сохраняется в поле, а не отписывается новой лямбдой:
        // каждый новый экземпляр делегата — другой объект, и -= с ним не снимал
        // бы подписку. Сервер живёт дольше контроллера при пересоздании окна,
        // и подписчик остался бы в его списке навсегда.
        _onClientsChanged = _ => RaiseDockStatusChanged();
        _dock.ClientsChanged += _onClientsChanged;
    }

    /// <summary>Поднимает сервер дока по текущим настройкам. Ошибка — в <see cref="DockError"/>.</summary>
    public bool StartDock()
    {
        _dockError = "";
        try
        {
            _dock.Start(DockSettings.EffectivePort);
        }
        catch (Exception ex)
        {
            _dockError = ex.Message;
            _logger.LogWarning(ex, "Док OBS: сервер не поднят — {Message}", ex.Message);
        }

        RaiseDockStatusChanged();
        return _dockError.Length == 0;
    }

    public void StopDock()
    {
        _dock.Stop();
        RaiseDockStatusChanged();
    }

    /// <summary>
    /// Поднимает сервер на конкретном порту (кнопка «Start server» и установка
    /// дока в OBS, где адрес должен совпасть с настройкой). Порт попутно
    /// фиксируется в настройках — иначе следующий автостарт поднял бы сервер
    /// на старом порту, и док OBS перестал бы открываться.
    /// </summary>
    public bool StartDockFor(int port, out string error)
    {
        DockSettings.Port = port;
        bool started = StartDock();
        error = _dockError;
        return started;
    }

    /// <summary>
    /// Применяет настройки из окна: список каналов, порт, автостарт. Сервер
    /// перезапускается только если изменился порт, а при отключении — глушится.
    /// </summary>
    public void ApplyDockSettings(ObsDockSettings updated, bool saveNow = true)
    {
        var target = DockSettings;
        int oldPort = target.EffectivePort;

        target.Enabled = updated.Enabled;
        target.Port = updated.Port;
        target.ShowAllInputs = updated.ShowAllInputs;
        target.ShowAllOutputs = updated.ShowAllOutputs;
        target.Channels = updated.Channels.Select(c => c.Clone()).ToList();

        _dockError = "";
        if (!target.Enabled)
        {
            _dock.Stop();
        }
        else if (_dock.IsRunning && oldPort == target.EffectivePort)
        {
            // Порт тот же — перезапуск не нужен, смена списка каналов дойдёт
            // до панели ближайшим снимком.
        }
        else if (!StartDock())
        {
            // Текст ошибки уже в DockError — окно покажет его пользователю.
        }

        if (saveNow) _settingsService.Save();
        RaiseDockStatusChanged();
    }

    /// <summary>Прописывает док в конфигурацию OBS (нужно перезапустить OBS).</summary>
    public ObsDockInstallResult InstallDockInObs() => _installer.Install(DockUrl);

    public ObsDockInstallResult RemoveDockFromObs() => _installer.Uninstall();

    /// <summary>Каналы, которые сейчас уедут в док (для окна настроек).</summary>
    public IReadOnlyList<ObsDockChannelState> GetDockChannelPreview() => BuildDockState().Channels;

    private void RaiseDockStatusChanged()
    {
        if (DockStatusChanged is null) return;

        // Событие прилетает из сетевых потоков — отдаём его в UI-поток.
        try
        {
            _ = _dispatcherService.InvokeAsync(() => DockStatusChanged?.Invoke());
        }
        catch (Exception)
        {
            // Приложение закрывается — подписчику это уже не нужно.
        }
    }

    /// <summary>
    /// Снимок для панели. Собирается на UI-таймере метров, поэтому читать
    /// свойства стрипов здесь безопасно, а команды дока применяются в том же
    /// потоке — рассинхрон между панелью и микшером невозможен по построению.
    /// </summary>
    public ObsDockState BuildDockState()
    {
        var settings = DockSettings;
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var selectedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in settings.Channels)
        {
            if (!string.IsNullOrEmpty(reference.StripId)) selectedIds.Add(reference.StripId);
            if (!string.IsNullOrEmpty(reference.DeviceId)) selectedDevices.Add(reference.DeviceId);
        }

        var channels = new List<ObsDockChannelState>();

        bool Picked(string kind, string stripId, string deviceId) =>
            selectedIds.Contains(stripId) || (!string.IsNullOrEmpty(deviceId) && selectedDevices.Contains(deviceId));

        foreach (var vm in _strips.Inputs)
        {
            if (!settings.ShowAllInputs && !Picked(ObsDockChannels.Input, vm.Id, vm.DeviceId)) continue;
            channels.Add(ToDockState(ObsDockChannels.Input, vm.Id, vm.Title, vm.DeviceId, vm.Name,
                vm.VolumeDb, vm.PeakLevel, vm.IsMuted, vm.IsSolo, vm.IsMono, true));
        }

        foreach (var vm in _strips.Buses)
        {
            if (!settings.ShowAllOutputs && !Picked(ObsDockChannels.Output, vm.Id, vm.DeviceId)) continue;
            channels.Add(ToDockState(ObsDockChannels.Output, vm.Id, vm.Title, vm.DeviceId, vm.Name,
                vm.VolumeDb, vm.PeakLevel, vm.IsMuted, vm.IsSolo, vm.IsMono, vm.IsAvailable));
        }

        return new ObsDockState(_engine.IsRunning, channels);
    }

    private static ObsDockChannelState ToDockState(string kind, string id, string title, string deviceId,
        string deviceName, float volumeDb, float peak, bool muted, bool solo, bool mono, bool available) =>
        new(id, kind, title, deviceId, deviceName, volumeDb, peak, muted, solo, mono, available);

    /// <summary>Публикация снимка, если к серверу подключена хотя бы одна панель.</summary>
    public void PublishDockState()
    {
        if (!_dock.IsRunning || _dock.ClientCount == 0) return;

        try
        {
            _dock.Publish(BuildDockState());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Док OBS: снимок состояния не отправлен");
        }
    }

    /// <summary>Команда из панели: громкость/mute/solo/mono стрипа.</summary>
    private void OnDockCommand(ObsDockCommand command)
    {
        try
        {
            _ = _dispatcherService.InvokeAsync(() => ApplyDockCommand(command));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Док OBS: команда {Op} не применена (нет UI-потока)", command.Op);
        }
    }

    private void ApplyDockCommand(ObsDockCommand command)
    {
        if (command.Kind == ObsDockChannels.Input)
        {
            foreach (var vm in _strips.Inputs)
            {
                if (vm.Id != command.Id) continue;
                ApplyChannelCommand(command,
                    v => vm.VolumeDb = v,
                    m => vm.IsMuted = m,
                    s => vm.IsSolo = s,
                    o => vm.IsMono = o);
                return;
            }

            _logger.LogDebug("Док OBS: команда {Op} для неизвестного входа {Id}", command.Op, command.Id);
            return;
        }

        foreach (var vm in _strips.Buses)
        {
            if (vm.Id != command.Id) continue;
            ApplyChannelCommand(command,
                v => vm.VolumeDb = v,
                m => vm.IsMuted = m,
                s => vm.IsSolo = s,
                o => vm.IsMono = o);
            return;
        }

        _logger.LogDebug("Док OBS: команда {Op} для неизвестного выхода {Id}", command.Op, command.Id);
    }

    private static void ApplyChannelCommand(ObsDockCommand command, Action<float> setVolume,
        Action<bool> setMute, Action<bool> setSolo, Action<bool> setMono)
    {
        switch (command.Op)
        {
            case ObsDockOps.Volume:
                setVolume(Math.Clamp(command.Value, -60f, 12f));
                break;
            case ObsDockOps.Mute:
                setMute(command.Flag);
                break;
            case ObsDockOps.Solo:
                setSolo(command.Flag);
                break;
            case ObsDockOps.Mono:
                setMono(command.Flag);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dock.CommandReceived -= OnDockCommand;
        _dock.ClientsChanged -= _onClientsChanged;
    }
}