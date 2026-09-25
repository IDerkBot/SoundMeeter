using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SoundMeeter.ViewModels;

// Устройства, списки приложений, поиск и постоянные правила маршрутизации.
public partial class MainViewModel
{
    private readonly IAudioService _audioService;
    private readonly IInstalledAppsService _installedAppsService;
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ObservableCollection<AudioDeviceViewModel> _allAudioDevices = new();
    private readonly SemaphoreSlim _appRoutingGate = new(1, 1);

    // Отфильтрованная коллекция для UI (только видимые устройства)
    public ObservableCollection<AudioDeviceViewModel> HiddenDevices { get; } = new();

    public ObservableCollection<AppViewModel> RunningApps { get; } = new();
    public ObservableCollection<InstalledAppViewModel> InstalledApps { get; } = new();

    public ICollectionView FilteredInstalledApps { get; }

    [ObservableProperty]
    private bool _isLoadingInstalledApps = false;

    [ObservableProperty]
    private string _installedAppsSearchText = string.Empty;

    partial void OnInstalledAppsSearchTextChanged(string value)
    {
        FilteredInstalledApps.Refresh();
    }

    private bool FilterInstalledAppsPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(InstalledAppsSearchText)) return true;
        if (item is InstalledAppViewModel app)
        {
            var search = InstalledAppsSearchText.ToLowerInvariant();
            return app.Name.ToLowerInvariant().Contains(search) ||
                   (!string.IsNullOrEmpty(app.Publisher) && app.Publisher.ToLowerInvariant().Contains(search));
        }
        return false;
    }

    public void AddPersistentRoute(InstalledAppViewModel app, string deviceId)
    {
        if (string.IsNullOrEmpty(app.ExecutablePath)) return;

        var rules = _settingsService.Settings.PersistentRoutes;
        lock (rules)
        {
            rules.RemoveAll(r => r.ExecutablePath.Equals(app.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            rules.Add(new DeviceRouteRule
            {
                ExecutablePath = app.ExecutablePath,
                DeviceId = deviceId,
                AppName = app.Name,
                IconPath = app.App.IconPath
            });
            _settingsService.Save();
        }
        RefreshConfiguredAppsInUI();
    }

    public void RemovePersistentRoute(ConfiguredAppViewModel app)
    {
        var rules = _settingsService.Settings.PersistentRoutes;
        lock (rules)
        {
            rules.RemoveAll(r => r.ExecutablePath.Equals(app.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            _settingsService.Save();
        }
        RefreshConfiguredAppsInUI();
    }

    /// <summary>
    /// Скрывает/показывает устройство (переключатель): помечает DeviceId в
    /// AppSettings.HiddenDeviceIds, сохраняет и перестраивает список стрипов.
    /// </summary>
    private void HideDevice(AudioDeviceViewModel device)
    {
        lock (_settingsService.Settings.PersistentRoutes)
        {
            if (!_settingsService.Settings.HiddenDeviceIds.Contains(device.Id))
                _settingsService.Settings.HiddenDeviceIds.Add(device.Id);
            else
                _settingsService.Settings.HiddenDeviceIds.Remove(device.Id);
            _settingsService.Save();
        }
        _ = RefreshDevicesAsync();
    }

    private void RefreshConfiguredAppsInUI()
    {
        _dispatcherService.InvokeAsync(() =>
        {
            foreach (var device in _allAudioDevices)
            {
                device.ConfiguredApps.Clear();
            }

            foreach (var rule in GetRouteRules())
            {
                var deviceVm = _allAudioDevices.FirstOrDefault(d => d.Id == rule.DeviceId);
                if (deviceVm != null)
                {
                    deviceVm.ConfiguredApps.Add(new ConfiguredAppViewModel(rule.AppName, rule.ExecutablePath, rule.IconPath, rule.DeviceId));
                }
            }
            RefreshStripApps();
        });
    }

    // Списки стрипов сопоставляются с endpoint, а не с внутренним Id канала.
    private void RefreshStripApps()
    {
        var rules = GetRouteRules();
        foreach (var strip in Inputs)
        {
            strip.AssignedApps.Clear();
            strip.ConfiguredApps.Clear();
            if (strip.IsMicrophone || string.IsNullOrEmpty(strip.Model.DeviceId)) continue;

            foreach (var app in RunningApps.Where(a => a.App.HasExplicitRoute &&
                         string.Equals(a.CurrentDeviceId, strip.Model.DeviceId, StringComparison.OrdinalIgnoreCase)))
                strip.AssignedApps.Add(app);

            foreach (var rule in rules.Where(r =>
                         string.Equals(r.DeviceId, strip.Model.DeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                if (strip.AssignedApps.Any(a => string.Equals(a.ExecutablePath, rule.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase))) continue;
                strip.ConfiguredApps.Add(new ConfiguredAppViewModel(rule.AppName, rule.ExecutablePath, rule.IconPath,
                    strip.Model.DeviceId));
            }
        }
    }

    private DeviceRouteRule[] GetRouteRules()
    {
        var rules = _settingsService.Settings.PersistentRoutes;
        lock (rules) return rules.ToArray();
    }

    public async Task<(bool Success, string Error)> AssignAppToStripAsync(object app, InputChannelViewModel strip)
    {
        if (!Inputs.Contains(strip))
            return (false, "Стрип не найден. Обновите список каналов.");

        string? path;
        string name;
        string icon;
        switch (app)
        {
            case AppViewModel running:
                path = running.ExecutablePath;
                name = running.Name;
                icon = path ?? "";
                break;
            case InstalledAppViewModel installed:
                path = installed.ExecutablePath;
                name = installed.Name;
                icon = installed.App.IconPath ?? "";
                break;
            case ConfiguredAppViewModel configured:
                path = configured.ExecutablePath;
                name = configured.Name;
                icon = path;
                break;
            default:
                return (false, "Неизвестный тип приложения.");
        }

        if (string.IsNullOrWhiteSpace(path) && app is not AppViewModel)
            return (false, "Не найден путь к исполняемому файлу приложения.");

        await _appRoutingGate.WaitAsync();
        try
        {
            var stripId = strip.Id;
            if (!_engine.Inputs.Any(i => i.Id == stripId))
                return (false, "Стрип был удалён. Повторите перенос.");

            // Куда уйдёт звук: на render-устройство, которое снимает стрип.
            var targetDeviceId = strip.Model.DeviceId;
            string? note = null;
            var routeHintStripId = stripId;
            if (!strip.HasAppSource)
            {
                var carrier = ResolveAppCarrierDevice(app);
                if (carrier == null)
                    return (false, "Не удалось определить устройство вывода приложения. " +
                                   "Задайте стрипу источник вручную (клик по имени источника).");

                // Устройство снимается только одним стрипом: если его уже держит другой
                // стрип, звук приложения физически находится именно в его канале.
                var owner = _engine.Inputs.FirstOrDefault(i =>
                    i.Id != stripId && DeviceEquals(i.DeviceId, carrier));
                if (owner != null)
                {
                    targetDeviceId = carrier;
                    routeHintStripId = owner.Id;
                    note = $"Устройство «{DeviceName(carrier)}» уже снимается стрипом «{StripTitle(owner)}» — " +
                           "приложение попало в его канал";
                }
                else
                {
                    var previous = strip.Model.Name;
                    _engine.SetInputSource(stripId, carrier);
                    var attached = _engine.Inputs.FirstOrDefault(i => i.Id == stripId);
                    if (attached == null || !DeviceEquals(attached.DeviceId, carrier))
                        return (false, $"Не удалось закрепить стрип за устройством «{DeviceName(carrier)}».");

                    // Loopback этого же устройства в его же выход = петля обратной связи.
                    var loopBus = _engine.Buses.FirstOrDefault(b =>
                        DeviceEquals(b.DeviceId, carrier) &&
                        attached.BusRouting.GetValueOrDefault(b.Id)?.Enabled == true);
                    if (loopBus != null) _engine.SetRoute(stripId, loopBus.Id, false);

                    targetDeviceId = carrier;
                    note = $"Стрип «{StripTitle(attached)}» переведён на loopback «{DeviceName(carrier)}»" +
                           (string.IsNullOrWhiteSpace(previous) ? "" : $" (был {previous})") +
                           (loopBus == null ? "" : $"; маршрут в «{loopBus.Name}» отключён во избежание петли");
                }
            }

            // Сохраняем цель до обращения к Windows, чтобы фоновый опрос не вернул старое правило.
            if (!string.IsNullOrWhiteSpace(path))
            {
                var rules = _settingsService.Settings.PersistentRoutes;
                lock (rules)
                {
                    rules.RemoveAll(r => string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                    rules.Add(new DeviceRouteRule
                    {
                        ExecutablePath = path, DeviceId = targetDeviceId, AppName = name, IconPath = icon
                    });
                    _settingsService.Save();
                }
            }

            var targets = RunningApps.Where(a => !string.IsNullOrWhiteSpace(path) &&
                string.Equals(a.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).Select(a => a.ProcessId).ToList();
            if (app is AppViewModel current) targets.Add(current.ProcessId);
            var errors = new List<string>();
            foreach (var pid in targets.Distinct())
            {
                var result = await _audioService.SetAppAudioDeviceAsync(pid, targetDeviceId);
                if (!result.Success) errors.Add(result.Error);
            }

            await RefreshAppsCoreAsync();

            var liveStrip = Inputs.FirstOrDefault(i => i.Id == routeHintStripId);
            var hint = liveStrip == null
                ? ""
                : $". Включите OUT/VIRT на стрипе «{liveStrip.Title}», чтобы направить канал";
            Status = errors.Count > 0
                ? "Правило сохранено; перенаправление пока не выполнено"
                : note == null
                    ? $"{name} → {Inputs.FirstOrDefault(i => i.Id == stripId)?.Title ?? DeviceName(targetDeviceId)}"
                    : $"{note}. {name} → {Inputs.FirstOrDefault(i => i.Id == stripId)?.Title ?? DeviceName(targetDeviceId)}{hint}";
            return (errors.Count == 0, string.Join(Environment.NewLine, errors.Distinct()));
        }
        finally { _appRoutingGate.Release(); }
    }

    private static bool DeviceEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string StripTitle(InputChannelModel model) =>
        string.IsNullOrWhiteSpace(model.ChannelName) ? model.Name : model.ChannelName;

    private string DeviceName(string? deviceId) =>
        _engine.Catalog.FirstOrDefault(d => DeviceEquals(d.DeviceId, deviceId))?.Name
        ?? _allAudioDevices.FirstOrDefault(d => DeviceEquals(d.Id, deviceId))?.Name
        ?? deviceId ?? "";

    /// <summary>true — известное нам устройство вывода (loopback, а не микрофон).</summary>
    private bool IsRenderDevice(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        (_engine.Catalog.Any(d => !d.IsMicrophone && DeviceEquals(d.DeviceId, deviceId)) ||
         _allAudioDevices.Any(d => DeviceEquals(d.Id, deviceId)));

    /// <summary>
    /// Render-устройство, через которое звук приложения попадёт в стрип: то, куда
    /// приложение играет сейчас, иначе (приложение не запущено) — вывод по умолчанию.
    /// </summary>
    private string? ResolveAppCarrierDevice(object app)
    {
        switch (app)
        {
            case AppViewModel running when IsRenderDevice(running.CurrentDeviceId):
                return running.CurrentDeviceId;
            case ConfiguredAppViewModel { DeviceId: var id } when IsRenderDevice(id):
                return id;
        }

        var byDefault = _allAudioDevices.FirstOrDefault(d => d.IsDefault)?.Id;
        if (IsRenderDevice(byDefault)) return byDefault;

        return _engine.Catalog.FirstOrDefault(d => !d.IsMicrophone)?.DeviceId;
    }

    public async Task<(bool Success, string Error)> RemoveStripAppAsync(object app)
    {
        await _appRoutingGate.WaitAsync();
        try
        {
            var path = app switch
            {
                AppViewModel running => running.ExecutablePath,
                ConfiguredAppViewModel configured => configured.ExecutablePath,
                _ => null
            };
            var rules = _settingsService.Settings.PersistentRoutes;
            lock (rules)
            {
                rules.RemoveAll(r => !string.IsNullOrWhiteSpace(path) &&
                    string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                // Windows сохраняет выход и после закрытия процесса. Сбросим его при следующей сессии.
                if (!string.IsNullOrWhiteSpace(path))
                    rules.Add(new DeviceRouteRule { ExecutablePath = path, DeviceId = "" });
                _settingsService.Save();
            }

            var targets = RunningApps.Where(a => !string.IsNullOrWhiteSpace(path) &&
                string.Equals(a.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).Select(a => a.ProcessId).ToList();
            if (app is AppViewModel current) targets.Add(current.ProcessId);
            var errors = new List<string>();
            foreach (var pid in targets.Distinct())
            {
                var result = await _audioService.SetAppAudioDeviceAsync(pid, "");
                if (!result.Success) errors.Add(result.Error);
            }
            await RefreshAppsCoreAsync();
            Status = errors.Count == 0
                ? (targets.Count == 0 ? "Привязка удалена: сброс выхода запланирован при запуске" : "Привязка удалена: используется системный выход")
                : "Не удалось сбросить выход приложения";
            return (errors.Count == 0, string.Join(Environment.NewLine, errors.Distinct()));
        }
        finally { _appRoutingGate.Release(); }
    }

    private async Task InitializeAsync()
    {
        await RefreshDataAsync();
        await LoadInstalledAppsAsync();
    }

    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        await RefreshDevicesAsync();
        await RefreshAppsAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await _audioService.GetAudioOutputDevicesAsync();

        await _dispatcherService.InvokeAsync(() =>
        {
            // Сохраняем текущие назначенные приложения
            var existingAssigned = _allAudioDevices.ToDictionary(d => d.Id, d => d.AssignedApps.ToList());

            _allAudioDevices.Clear();
            HiddenDevices.Clear();

            foreach (var device in devices)
            {
                var vm = new AudioDeviceViewModel(_audioService, HideDevice, RemovePersistentRoute);
                vm.Device = device;

                if (existingAssigned.TryGetValue(device.Id, out var apps))
                {
                    foreach (var app in apps) vm.AssignedApps.Add(app);
                }

                _allAudioDevices.Add(vm);

                // Если устройство было скрыто ранее, добавляем его в список скрытых
                if (_settingsService.Settings.HiddenDeviceIds.Contains(device.Id))
                {
                    HiddenDevices.Add(vm);
                }
            }
            RefreshConfiguredAppsInUI();
        });
    }

    private async Task RefreshAppsAsync()
    {
        if (!await _appRoutingGate.WaitAsync(0)) return;
        try { await RefreshAppsCoreAsync(); }
        finally { _appRoutingGate.Release(); }
    }

    private async Task RefreshAppsCoreAsync()
    {
        var apps = await _audioService.GetRunningAppsWithAudioAsync();
        await _dispatcherService.InvokeAsync(() =>
        {
            RunningApps.Clear();
            foreach (var app in apps) RunningApps.Add(new AppViewModel(app, _audioService));
            DistributeAppsToDevices(apps);
            RefreshStripApps();
        });
    }

    private void DistributeAppsToDevices(List<RunningApp> apps)
    {
        foreach (var device in _allAudioDevices) device.AssignedApps.Clear();

        foreach (var app in apps)
        {
            var deviceVm = _allAudioDevices.FirstOrDefault(d => d.Id == app.CurrentDeviceId);
            if (deviceVm != null)
            {
                deviceVm.AssignedApps.Add(new AppViewModel(app, _audioService));
            }
        }
    }

    public async Task<(bool Success, string Error)> AssignAppToDeviceAsync(uint processId, string deviceId)
    {
        var result = await _audioService.SetAppAudioDeviceAsync(processId, deviceId);
        if (result.Success) await RefreshAppsAsync();
        return result;
    }

    [RelayCommand]
    private async Task LoadInstalledAppsAsync()
    {
        IsLoadingInstalledApps = true;
        try
        {
            var apps = await _installedAppsService.GetInstalledAppsAsync();
            await _dispatcherService.InvokeAsync(() =>
            {
                InstalledApps.Clear();
                foreach (var app in apps) InstalledApps.Add(new InstalledAppViewModel(app));
                FilteredInstalledApps.Refresh();
            });
        }
        finally { IsLoadingInstalledApps = false; }
    }
}
