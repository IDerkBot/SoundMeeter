using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Главная VM: жизненный цикл и управление микшером.
/// MIDI, маршрутизация приложений и сохранение вынесены в тематические partial-файлы.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly DispatcherTimer _meterTimer;

    [ObservableProperty]
    private ObservableCollection<InputChannelViewModel> _inputs = new();

    [ObservableProperty]
    private ObservableCollection<OutputBusViewModel> _buses = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasVirtualCable;

    [ObservableProperty]
    private string _status = "";

    public MainViewModel(
        IAudioEngine engine,
        SettingsService settings,
        IMidiService midi,
        IAudioService audioService,
        IInstalledAppsService installedAppsService,
        ISettingsService settingsService,
        IDispatcherService dispatcherService,
        IUpdateService updateService)
    {
        _audioService = audioService;
        _installedAppsService = installedAppsService;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _updateService = updateService;
        _engine = engine;
        _settings = settings;
        _midi = midi;
        _engine.ChannelsChanged += OnChannelsChanged;
        _engine.StateChanged += OnStateChanged;
        _midi.MessageReceived += OnMidiMessageReceived;

        _audioService.AudioDevicesChanged += async (s, e) => await RefreshDevicesAsync();
        _audioService.AppsChanged += async (s, e) => await RefreshAppsAsync();

        FilteredInstalledApps = CollectionViewSource.GetDefaultView(InstalledApps);
        FilteredInstalledApps.Filter = FilterInstalledAppsPredicate;

        _ = InitializeAsync();

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        _saveTimer = new System.Threading.Timer(_ => SavePool(), null, 5000, 2000);
    }

    [RelayCommand]
    public void Refresh() => _engine.RefreshDevices();

    [RelayCommand]
    public void AddInput() => _engine.AddInput();

    [RelayCommand]
    public void AddBus() => _engine.AddBus();

    [RelayCommand]
    public void RemoveInput(string inputId) => _engine.RemoveInput(inputId);

    [RelayCommand]
    public void RemoveBus(string busId) => _engine.RemoveBus(busId);

    [RelayCommand]
    public void ToggleRun()
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
    }

    /// <summary>
    /// Доступ к движку (для назначения источника через пикер).
    /// </summary>
    public IAudioEngine Engine => _engine;

    /// <summary>
    /// Полное завершение: останавливает таймеры и движок, освобождает
    /// NAudio-устройства, чтобы процесс мог корректно завершиться.
    /// </summary>
    public void Shutdown()
    {
        _saveTimer.Dispose();
        _meterTimer.Stop();
        _engine.Stop();
        _engine.ChannelsChanged -= OnChannelsChanged;
        _engine.StateChanged -= OnStateChanged;
        _midi.MessageReceived -= OnMidiMessageReceived;
        _midi.Close();
    }

    private void UpdateMeters()
    {
        foreach (var vm in Inputs)
            vm.UpdatePeak(vm.Model.PeakLevel);
        foreach (var vm in Buses)
            vm.UpdatePeak(vm.Model.PeakLevel);
    }

    private void OnChannelsChanged()
    {
        Inputs.Clear();
        foreach (var model in _engine.Inputs)
            Inputs.Add(new InputChannelViewModel(model, _engine, _engine.Buses, MarkDirty));

        Buses.Clear();
        foreach (var model in _engine.Buses)
            Buses.Add(new OutputBusViewModel(_engine, model, MarkDirty));

        HasVirtualCable = _engine.Buses.Any(b =>
            b.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
            b.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));

        Status = $"{_engine.Inputs.Count} inputs / {_engine.Buses.Count} buses";
        RefreshStripApps();
        MarkDirty();
    }

    private void OnStateChanged()
    {
        IsRunning = _engine.IsRunning;
        Status = IsRunning ? "Engine: running" : "Engine: stopped";
    }
}
