using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.AppRouting.Contract;
using SoundMeeter.AppRouting.Entities;
using SoundMeeter.AppRouting.Presentation.ViewModels;
using SoundMeeter.Audio.Contract;
using SoundMeeter.Audio.Entities;
using SoundMeeter.Audio.Presentation.ViewModels;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Infrastructure.Entities;

namespace SoundMeeter.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // Устройства, приложения, поиск и маршрутизация
    // =========================================================================
    public ObservableCollection<AudioDeviceViewModel> HiddenDevices { get; } = new();
    public ObservableCollection<AppViewModel> RunningApps { get; } = new();
    public ObservableCollection<InstalledAppViewModel> InstalledApps { get; } = new();
    public ObservableCollection<InstalledAppViewModel> FilteredInstalledApps { get; } = new();

    [ObservableProperty]
    private bool _isLoadingInstalledApps = false;

    [ObservableProperty]
    private string _installedAppsSearchText = string.Empty;

    partial void OnInstalledAppsSearchTextChanged(string value) => RefilterInstalledApps();

    private void RefilterInstalledApps()
    {
        FilteredInstalledApps.Clear();

        if (string.IsNullOrWhiteSpace(InstalledAppsSearchText))
        {
            foreach (var app in InstalledApps) FilteredInstalledApps.Add(app);
            return;
        }

        var search = InstalledAppsSearchText.ToLowerInvariant();
        foreach (var app in InstalledApps)
        {
            if (app.Name.ToLowerInvariant().Contains(search) ||
                (!string.IsNullOrEmpty(app.Publisher) && app.Publisher.ToLowerInvariant().Contains(search)))
            {
                FilteredInstalledApps.Add(app);
            }
        }
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
                device.ConfiguredApps.Clear();

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

    private void RefreshStripApps()
    {
        var rules = GetRouteRules();
        foreach (var strip in Inputs)
        {
            strip.AssignedApps.Clear();
            strip.ConfiguredApps.Clear();

            var appDeviceId = strip.AppSourceDeviceId;
            if (string.IsNullOrEmpty(appDeviceId)) continue;

            foreach (var app in RunningApps.Where(a => a.App.HasExplicitRoute &&
                         string.Equals(a.CurrentDeviceId, appDeviceId, StringComparison.OrdinalIgnoreCase)))
                strip.AssignedApps.Add(app);

            foreach (var rule in rules.Where(r => string.Equals(r.DeviceId, appDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                if (Enumerable.Any<AppViewModel>(strip.AssignedApps, a => string.Equals(a.ExecutablePath, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase))) 
                    continue;
                    
                strip.ConfiguredApps.Add(new ConfiguredAppViewModel(rule.AppName, rule.ExecutablePath, rule.IconPath, appDeviceId));
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
            return (false, Loc.Get("Sm.Routing.StripNotFound"));

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
                return (false, Loc.Get("Sm.Routing.UnknownAppType"));
        }

        if (string.IsNullOrWhiteSpace(path) && app is not AppViewModel)
            return (false, Loc.Get("Sm.Routing.ExeNotFound"));

        await _appRoutingGate.WaitAsync();
        try
        {
            var stripId = strip.Id;
            if (!Enumerable.Any<InputChannelModel>(_engine.Inputs, i => i.Id == stripId))
                return (false, Loc.Get("Sm.Routing.StripRemoved"));

            if (strip.AppSourceDeviceId is not { } targetDeviceId)
                return (false, strip.AppRejectReason());

            if (!string.IsNullOrWhiteSpace(path))
            {
                var rules = _settingsService.Settings.PersistentRoutes;
                lock (rules)
                {
                    rules.RemoveAll(r => string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                    rules.Add(new DeviceRouteRule { ExecutablePath = path, DeviceId = targetDeviceId, AppName = name, IconPath = icon });
                    _settingsService.Save();
                }
            }

            var targets = RunningApps.Where(a => !string.IsNullOrWhiteSpace(path) &&
                string.Equals(a.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).Select(a => a.ProcessId).ToList();
            
            if (app is AppViewModel current) 
                targets.Add(current.ProcessId);
                
            var errors = new List<string>();
            foreach (var pid in targets.Distinct())
            {
                var result = await _audioService.SetAppAudioDeviceAsync(pid, targetDeviceId);
                if (!result.Success) errors.Add(result.Error);
            }

            await RefreshAppsCoreAsync();

            var liveStrip = Enumerable.FirstOrDefault<InputChannelViewModel>(Inputs, i => i.Id == stripId);
            var hint = liveStrip == null ? "" : Loc.Get("Sm.Routing.EnableOutVirt", liveStrip.Title);
            
            Status = errors.Count > 0
                ? Loc.Get("Sm.Routing.SavedNoRedirect")
                : $"{name} → {liveStrip?.Title ?? DeviceName(targetDeviceId)}{hint}";
                
            return (errors.Count == 0, string.Join(Environment.NewLine, errors.Distinct()));
        }
        finally 
        { 
            _appRoutingGate.Release(); 
        }
    }

    private static bool DeviceEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string DeviceName(string? deviceId) =>
        Enumerable.FirstOrDefault<DeviceInfo>(_engine.Catalog, d => DeviceEquals(d.DeviceId, deviceId))?.Name
        ?? _allAudioDevices.FirstOrDefault(d => DeviceEquals(d.Id, deviceId))?.Name
        ?? deviceId ?? "";

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
                rules.RemoveAll(r => !string.IsNullOrWhiteSpace(path) && string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(path))
                    rules.Add(new DeviceRouteRule { ExecutablePath = path, DeviceId = "" });
                _settingsService.Save();
            }

            var targets = RunningApps.Where(a => !string.IsNullOrWhiteSpace(path) &&
                string.Equals(a.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).Select(a => a.ProcessId).ToList();
                
            if (app is AppViewModel current) 
                targets.Add(current.ProcessId);
                
            var errors = new List<string>();
            foreach (var pid in targets.Distinct())
            {
                var result = await _audioService.SetAppAudioDeviceAsync(pid, "");
                if (!result.Success) errors.Add(result.Error);
            }
            
            await RefreshAppsCoreAsync();
            Status = errors.Count == 0
                ? (targets.Count == 0 ? Loc.Get("Sm.Routing.UnbindPending") : Loc.Get("Sm.Routing.UnbindSystem"))
                : Loc.Get("Sm.Routing.UnbindFailed");
                
            return (errors.Count == 0, string.Join(Environment.NewLine, errors.Distinct()));
        }
        finally 
        { 
            _appRoutingGate.Release(); 
        }
    }

    public async Task InitializeAsync()
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
            foreach (var app in apps) RunningApps.Add(new AppViewModel(app));
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
                deviceVm.AssignedApps.Add(new AppViewModel(app));
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
                RefilterInstalledApps();
            });
        }
        finally { IsLoadingInstalledApps = false; }
    }
}
