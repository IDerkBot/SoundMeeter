using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace SoundMeeter.ViewModels;

/// <summary>Канал микшера в списке выбора для дока.</summary>
public sealed partial class ObsDockChannelItemViewModel : ObservableObject
{
    public ObsDockChannelItemViewModel(string id, string kind, string title, string deviceName, string deviceId,
        bool isAvailable)
    {
        Id = id;
        Kind = kind;
        Title = title;
        DeviceName = deviceName;
        DeviceId = deviceId;
        IsAvailable = isAvailable;
    }

    public string Id { get; }

    /// <summary>ObsDockChannels.Input или Output.</summary>
    public string Kind { get; }

    public string Title { get; }

    public string DeviceName { get; }

    /// <summary>Устройство стрипа: выбор переживает пересоздание стрипа движком.</summary>
    public string DeviceId { get; }

    public bool IsAvailable { get; }

    /// <summary>Показывать этот канал в доке (в дополнение к «включить все»).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Что показать в списке: имя канала, а если оно совпадает с устройством — только его.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? DeviceName : Title;

    public string Tooltip => string.IsNullOrWhiteSpace(DeviceName) || DeviceName == Title
        ? DisplayName
        : $"{Title}  ({DeviceName})";
}

/// <summary>
/// Окно настроек док-панели OBS: включать ли сервер, на каком порту, какие
/// каналы в него отдавать и как добавить сам док в OBS (правкой его user.ini).
/// Изменения применяются кнопкой Apply и сразу сохраняются в settings.json.
/// </summary>
public sealed partial class ObsDockSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;

    public ObsDockSettingsViewModel(MainViewModel main)
    {
        _main = main;

        var settings = main.DockSettings;
        Enabled = settings.Enabled;
        ShowAllInputs = settings.ShowAllInputs;
        ShowAllOutputs = settings.ShowAllOutputs;
        PortText = settings.EffectivePort.ToString();

        foreach (var vm in main.Inputs)
            InputChannels.Add(new ObsDockChannelItemViewModel(vm.Id, ObsDockChannels.Input, vm.Title,
                vm.Name, vm.Model.DeviceId, vm.Model.IsAvailable && !string.IsNullOrWhiteSpace(vm.Model.DeviceId)));

        foreach (var vm in main.Buses)
            OutputChannels.Add(new ObsDockChannelItemViewModel(vm.Id, ObsDockChannels.Output, vm.Title,
                vm.Name, vm.Model.DeviceId, vm.IsAvailable && !string.IsNullOrWhiteSpace(vm.Model.DeviceId)));

        MarkSelected(settings);

        _main.DockStatusChanged += RefreshStatus;
        RefreshStatus();
    }

    /// <summary>Поднимать сервер дока (в том числе автостарт при запуске приложения).</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Порт сервера (текстом — чтобы не мешать вводе).</summary>
    [ObservableProperty]
    private string _portText;

    /// <summary>Отдавать в док все входные стрипы.</summary>
    [ObservableProperty]
    private bool _showAllInputs;

    /// <summary>Отдавать в док все выходные шины.</summary>
    [ObservableProperty]
    private bool _showAllOutputs;

    /// <summary>Инпуты, отмеченные пользователем.</summary>
    public ObservableCollection<ObsDockChannelItemViewModel> InputChannels { get; } = new();

    /// <summary>Выходы, отмеченные пользователем.</summary>
    public ObservableCollection<ObsDockChannelItemViewModel> OutputChannels { get; } = new();

    /// <summary>Индикатор подключённых панелей.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>Результат установки/удаления дока в OBS (зелёный/красный — по IsStatusOk).</summary>
    [ObservableProperty]
    private string _obsStatus = "";

    [ObservableProperty]
    private bool _isStatusOk;

    public string DockUrl => _main.DockUrl;

    public bool IsServerRunning => _main.IsDockRunning;

    /// <summary>Текст кнопки: сервер можно и поднять, и погасить прямо из окна.</summary>
    public string ServerToggleText => _main.IsDockRunning ? "Stop server" : "Start server";

    /// <summary>Док уже прописан в конфигурации OBS.</summary>
    public string ObsDockStateText => ObsDockInstaller.IsObsRunning()
        ? "OBS is running — close it before adding or removing the dock"
        : _main.IsDockInstalledInObs
            ? "Dock is registered in OBS (restart OBS to see it)"
            : "Dock is not registered in OBS";

    [RelayCommand]
    private void Apply()
    {
        if (!TryReadPort(out int port))
        {
            SetObsStatus($"Port {PortText} is not valid: use a number from 1024 to 65535.", false);
            return;
        }

        var updated = BuildSettings(port);
        _main.ApplyDockSettings(updated);
        RefreshStatus();

        SetObsStatus(_main.DockError.Length > 0
            ? _main.DockError
            : $"Saved. The panel will show {_main.GetDockChannelPreview().Count} channel(s) at {DockUrl}.",
            _main.DockError.Length == 0);
    }

    [RelayCommand]
    private void ToggleServer()
    {
        if (_main.IsDockRunning)
        {
            _main.StopDock();
        }
        else if (!TryReadPort(out int port))
        {
            SetObsStatus($"Port {PortText} is not valid: use a number from 1024 to 65535.", false);
            return;
        }
        else
        {
            // Порт применяем сразу, иначе сервер поднимется на старом.
            _main.ApplyDockSettings(BuildSettings(port));
        }

        OnPropertyChanged(nameof(ServerToggleText));
        OnPropertyChanged(nameof(IsServerRunning));
        RefreshStatus();

        if (_main.DockError.Length > 0) SetObsStatus(_main.DockError, false);
    }

    [RelayCommand]
    private void CopyUrl()
    {
        try
        {
            Clipboard.SetText(DockUrl);
            SetObsStatus("URL copied. In OBS: View → Docks → Browser Dock → paste the URL.", true);
        }
        catch (Exception ex)
        {
            SetObsStatus($"Failed to copy URL: {ex.Message}", false);
        }
    }

    [RelayCommand]
    private void InstallInObs()
    {
        if (!TryReadPort(out int port))
        {
            SetObsStatus($"Port {PortText} is not valid: use a number from 1024 to 65535.", false);
            return;
        }

        // Адрес в OBS обязан совпадать с тем, на котором реально работает сервер.
        if (!_main.IsDockRunning || _main.DockServer.Port != port)
        {
            if (!_main.StartDockFor(port, out var startError))
            {
                SetObsStatus(startError, false);
                return;
            }
        }

        var result = _main.InstallDockInObs();
        SetObsStatus(result.Message, result.Success);
        OnPropertyChanged(nameof(ObsDockStateText));
        OnPropertyChanged(nameof(ServerToggleText));
        OnPropertyChanged(nameof(IsServerRunning));
        RefreshStatus();
    }

    [RelayCommand]
    private void RemoveFromObs()
    {
        var result = _main.RemoveDockFromObs();
        SetObsStatus(result.Message, result.Success);
        OnPropertyChanged(nameof(ObsDockStateText));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in InputChannels.Concat(OutputChannels)) item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in InputChannels.Concat(OutputChannels)) item.IsSelected = false;
    }

    partial void OnShowAllInputsChanged(bool value) => OnPropertyChanged(nameof(InputsGroupEnabled));

    partial void OnShowAllOutputsChanged(bool value) => OnPropertyChanged(nameof(OutputsGroupEnabled));

    /// <summary>false — список входов не влияет на док, пока включены «все входы».</summary>
    public bool InputsGroupEnabled => !ShowAllInputs;

    public bool OutputsGroupEnabled => !ShowAllOutputs;

    private ObsDockSettings BuildSettings(int port) => new()
    {
        Enabled = Enabled,
        Port = port,
        ShowAllInputs = ShowAllInputs,
        ShowAllOutputs = ShowAllOutputs,
        // DeviceId кладём рядом с Id стрипа: движок пересоздаёт стрип с новым Id
        // при каждом повторном обнаружении устройств, и по одному Id выбранный
        // канал молча исчез бы из дока после перезапуска приложения.
        Channels = InputChannels.Where(c => c.IsSelected)
            .Select(c => new ObsDockChannelRef(c.Id, c.DeviceId, ObsDockChannels.Input))
            .Concat(OutputChannels.Where(c => c.IsSelected)
                .Select(c => new ObsDockChannelRef(c.Id, c.DeviceId, ObsDockChannels.Output)))
            .ToList()
    };

    private bool TryReadPort(out int port)
    {
        return int.TryParse(PortText, out port) && port is >= 1024 and <= 65535;
    }

    private void MarkSelected(ObsDockSettings settings)
    {
        var ids = new HashSet<string>(settings.Channels.Select(c => c.StripId), StringComparer.Ordinal);
        var devices = new HashSet<string>(
            settings.Channels.Select(c => c.DeviceId).Where(d => !string.IsNullOrEmpty(d)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in InputChannels.Concat(OutputChannels))
            item.IsSelected = ids.Contains(item.Id) || (!string.IsNullOrEmpty(item.DeviceId) && devices.Contains(item.DeviceId));
    }

    private void SetObsStatus(string text, bool ok)
    {
        ObsStatus = text;
        IsStatusOk = ok;
    }

    private void RefreshStatus()
    {
        int clients = _main.DockClients;
        string server = _main.IsDockRunning ? $"server on port {_main.DockServer.Port}" : "server stopped";
        string panels = clients == 0 ? "no panels connected" : $"{clients} panel(s) connected";
        Status = _main.DockError.Length > 0 ? _main.DockError : $"{server}, {panels}";
    }

    public void Dispose() => _main.DockStatusChanged -= RefreshStatus;
}
