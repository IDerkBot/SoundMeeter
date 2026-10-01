using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.ViewModels;

// Док-панель в OBS: локальный сервер, снимок состояния для панели и применение
// её команд к стрипам микшера.
public partial class MainViewModel
{
    private readonly IObsDockServer _dock;
    private readonly ObsDockInstaller _dockInstaller = new();
    private readonly ILogger _dockLogger = AppLog.For<MainViewModel>();
    private string _dockError = "";

    /// <summary>Сервер док-панели. Пока панель не подключена, состояние не собирается вовсе.</summary>
    public IObsDockServer DockServer => _dock;

    /// <summary>Живые настройки дока (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public ObsDockSettings DockSettings => _settings.Settings.ObsDock;

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
    public bool IsDockInstalledInObs => _dockInstaller.IsDockInstalled();

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
            _dockLogger.LogWarning(ex, "Док OBS: сервер не поднят — {Message}", ex.Message);
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

        if (saveNow) SaveNow();
        RaiseDockStatusChanged();
    }

    /// <summary>Прописывает док в конфигурацию OBS (нужно перезапустить OBS).</summary>
    public ObsDockInstallResult InstallDockInObs() => _dockInstaller.Install(DockUrl);

    public ObsDockInstallResult RemoveDockFromObs() => _dockInstaller.Uninstall();

    /// <summary>Каналы, которые сейчас уедут в док (для окна настроек).</summary>
    public IReadOnlyList<ObsDockChannelState> GetDockChannelPreview() => BuildDockState().Channels;

    private void RaiseDockStatusChanged()
    {
        var handler = DockStatusChanged;
        if (handler is null) return;

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
    private ObsDockState BuildDockState()
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

        foreach (var vm in Inputs)
        {
            if (!settings.ShowAllInputs && !Picked(ObsDockChannels.Input, vm.Id, vm.Model.DeviceId)) continue;
            channels.Add(ToDockState(ObsDockChannels.Input, vm.Id, vm.Title, vm.Model.DeviceId, vm.Name,
                vm.VolumeDb, vm.PeakLevel, vm.IsMuted, vm.IsSolo, vm.IsMono, true));
        }

        foreach (var vm in Buses)
        {
            if (!settings.ShowAllOutputs && !Picked(ObsDockChannels.Output, vm.Id, vm.Model.DeviceId)) continue;
            channels.Add(ToDockState(ObsDockChannels.Output, vm.Id, vm.Title, vm.Model.DeviceId, vm.Name,
                vm.VolumeDb, vm.PeakLevel, vm.IsMuted, vm.IsSolo, vm.IsMono, vm.IsAvailable));
        }

        return new ObsDockState(_engine.IsRunning, channels);
    }

    private static ObsDockChannelState ToDockState(string kind, string id, string title, string deviceId,
        string deviceName, float volumeDb, float peak, bool muted, bool solo, bool mono, bool available) =>
        new(id, kind, title, deviceId, deviceName, volumeDb, peak, muted, solo, mono, available);

    /// <summary>Публикация снимка, если к серверу подключена хотя бы одна панель.</summary>
    private void PublishDockState()
    {
        if (!_dock.IsRunning || _dock.ClientCount == 0) return;

        try
        {
            _dock.Publish(BuildDockState());
        }
        catch (Exception ex)
        {
            _dockLogger.LogError(ex, "Док OBS: снимок состояния не отправлен");
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
            _dockLogger.LogError(ex, "Док OBS: команда {Op} не применена (нет UI-потока)", command.Op);
        }
    }

    private void ApplyDockCommand(ObsDockCommand command)
    {
        if (command.Kind == ObsDockChannels.Input)
        {
            foreach (var vm in Inputs)
            {
                if (vm.Id != command.Id) continue;
                ApplyChannelCommand(command,
                    v => vm.VolumeDb = v,
                    m => vm.IsMuted = m,
                    s => vm.IsSolo = s,
                    o => vm.IsMono = o);
                return;
            }

            _dockLogger.LogDebug("Док OBS: команда {Op} для неизвестного входа {Id}", command.Op, command.Id);
            return;
        }

        foreach (var vm in Buses)
        {
            if (vm.Id != command.Id) continue;
            ApplyChannelCommand(command,
                v => vm.VolumeDb = v,
                m => vm.IsMuted = m,
                s => vm.IsSolo = s,
                o => vm.IsMono = o);
            return;
        }

        _dockLogger.LogDebug("Док OBS: команда {Op} для неизвестного выхода {Id}", command.Op, command.Id);
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

    /// <summary>Подписка на события сервера. Вызывается из конструктора MainViewModel.</summary>
    private void SubscribeDock()
    {
        _dock.CommandReceived += OnDockCommand;
        _dock.ClientsChanged += _ => RaiseDockStatusChanged();
    }
}
