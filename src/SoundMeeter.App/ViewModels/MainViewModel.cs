using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.AppRouting.Contract;
using SoundMeeter.AppRouting.Entities;
using SoundMeeter.AppRouting.Presentation.ViewModels;
using SoundMeeter.Audio.Contract;
using SoundMeeter.Audio.Entities;
using SoundMeeter.Audio.Presentation;
using SoundMeeter.Audio.Presentation.ViewModels;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;
using SoundMeeter.Core.UseCases;
using SoundMeeter.Docking.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Infrastructure.Contract;
using SoundMeeter.Infrastructure.Contract.Implementation;
using SoundMeeter.Infrastructure.Entities;
using SoundMeeter.Midi.UseCases;
using SoundMeeter.TextToSpeech.Contract;
using SoundMeeter.TextToSpeech.Entities;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Twitch.Entities;
using SoundMeeter.Twitch.UseCases;
using SoundMeeter.Update.Entities;
using SoundMeeter.Update.Presentation.ViewModels;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Главная VM: коллекции полос для разметки и команды главного окна.
///
/// Всё, что живёт без участия пользователя — подписки на движок, таймер метров,
/// таймер сохранения, пресет, импорт и экспорт — ушло в <c>MixerCoordinator</c>.
/// Разделение не косметическое: пока VM держала и то и другое, она не могла уйти
/// из слоя сценариев, потому что создавала ViewModel'ы полос, а они нужны были
/// оркестрации.
///
/// MIDI, маршрутизация приложений и сохранение вынесены в тематические partial-файлы.
/// </summary>
public partial class MainViewModel : LocalizedViewModel, ITextToSpeechHost, ITwitchHost
{
    // =========================================================================
    // Поля и конструктор
    // =========================================================================
    private readonly IAudioEngine _engine;
    private readonly MixerCoordinator _coordinator;
    private readonly AppBehaviourService _behaviour;
    private readonly UpdateViewModel _updateVm;
    private readonly IAudioService _audioService;
    private readonly IInstalledAppsService _installedAppsService;
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;
    
    private readonly ObservableCollection<AudioDeviceViewModel> _allAudioDevices = new();
    private readonly SemaphoreSlim _appRoutingGate = new(1, 1);

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
        MixerCoordinator coordinator,
        AppBehaviourService behaviour,
        IAudioService audioService,
        IInstalledAppsService installedAppsService,
        ISettingsService settingsService,
        IDispatcherService dispatcherService,
        UpdateViewModel updateVm)
    {
        _engine = engine;
        _coordinator = coordinator;
        _behaviour = behaviour;
        _updateVm = updateVm;
        _audioService = audioService;
        _installedAppsService = installedAppsService;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;

        _coordinator.ChannelsChanged += OnChannelsChanged;
        _coordinator.StateChanged += OnStateChanged;
        _coordinator.MeterTick += OnMeterTick;

        _audioService.AudioDevicesChanged += async (s, e) => await RefreshDevicesAsync();
        _audioService.AppsChanged += async (s, e) => await RefreshAppsAsync();

        // Подписка на уведомления от UpdateViewModel: свойства-обёртки (UpdateStatus,
        // UpdateBanner, IsUpdateAvailable) должны уведомлять WPF привязки об изменениях.
        _updateVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.UpdateStatus) or
                                      nameof(UpdateViewModel.UpdateBanner) or
                                      nameof(UpdateViewModel.IsUpdateAvailable))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        _ = InitializeAsync();
        BuildLanguages();
    }

    // =========================================================================
    // Язык и основные команды
    // =========================================================================
    public string StartStopText => IsRunning ? Loc.Get("Sm.Toolbar.Stop") : Loc.Get("Sm.Toolbar.Start");

    [RelayCommand] public void Refresh() => _engine.RefreshDevices();
    [RelayCommand] public void AddInput() => _engine.AddInput();
    [RelayCommand] public void AddBus() => _engine.AddBus();
    [RelayCommand] public void RemoveInput(string inputId) => _engine.RemoveInput(inputId);
    [RelayCommand] public void RemoveBus(string busId) => _engine.RemoveBus(busId);

    public void MoveInput(int fromIndex, int toIndex) => _engine.MoveInput(fromIndex, toIndex);
    public void MoveBus(int fromIndex, int toIndex) => _engine.MoveBus(fromIndex, toIndex);

    [RelayCommand]
    public void ToggleRun()
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StartStopText));

    public IAudioEngine Engine => _engine;

    public void Shutdown()
    {
        _coordinator.MeterTick -= OnMeterTick;
        _coordinator.Shutdown();
    }

    private void OnMeterTick()
    {
        foreach (var vm in Inputs) vm.UpdatePeak(vm.Model.PeakLevel);
        foreach (var vm in Buses) vm.UpdatePeak(vm.Model.PeakLevel);
    }

    private void OnChannelsChanged()
    {
        foreach (var input in Inputs) input.Dispose();
        foreach (var bus in Buses) bus.Dispose();

        Inputs.Clear();
        foreach (var model in _engine.Inputs)
            Inputs.Add(new InputChannelViewModel(model, _engine, _engine.Buses, _engine.Catalog, _coordinator.MarkDirty));

        Buses.Clear();
        foreach (var model in _engine.Buses)
            Buses.Add(new OutputBusViewModel(_engine, model, _coordinator.MarkDirty, OnBusTitleChanged));

        _coordinator.PublishStrips(Inputs, Buses);
        HasVirtualCable = _engine.Catalog.Any(d => d.IsVirtualCable);
        Status = Loc.Get("Sm.Toolbar.StripCounts", _engine.Inputs.Count, _engine.Buses.Count);
        
        RefreshStripApps();
        _coordinator.MarkDirty();
    }

    private void OnStateChanged(bool isRunning)
    {
        IsRunning = isRunning;
        Status = IsRunning ? Loc.Get("Sm.Toolbar.EngineRunning") : Loc.Get("Sm.Toolbar.EngineStopped");
    }

    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(StartStopText));
    }

    protected override void RunOnUiThread(Action action)
    {
        if (_dispatcherService.HasThreadAccess) action();
        else _dispatcherService.Post(action);
    }

    protected override void DisposeCore()
    {
        _coordinator.ChannelsChanged -= OnChannelsChanged;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.MeterTick -= OnMeterTick;

        foreach (var input in Inputs) input.Dispose();
        foreach (var bus in Buses) bus.Dispose();
        foreach (var option in Languages) option.Dispose();
    }

    private void OnBusTitleChanged(OutputBusModel bus)
    {
        foreach (var input in Inputs)
            input.UpdateBusChannelName(bus.Id, bus.ChannelName);
    }

    // =========================================================================
    // Поведение приложения: трей и автозапуск
    // =========================================================================
    public Action<string, string>? NotifyTray { get; set; }
    public Action? OnApplyTraySettings { get; set; }

    // =========================================================================
    // MIDI
    // =========================================================================
    public MidiController Midi => _coordinator.Midi;

    // =========================================================================
    // Док-панель OBS
    // =========================================================================
    public DockController Dock => _coordinator.Dock;

    // =========================================================================
    // Пресет и сохранение
    // =========================================================================
    public void Restore(AppSettings? saved) => _coordinator.Restore(saved);
    public void SaveNow() => _coordinator.SaveNow();

    // =========================================================================
    // Устройства, приложения, поиск и маршрутизация
    // =========================================================================

    // =========================================================================
    // Модуль синтеза речи (TTS)
    // =========================================================================
    private TtsViewModel TtsVm => _coordinator.Tts;

    public TtsViewModel TtsModule => TtsVm;
    public TextToSpeechService Tts => TtsVm.Service;
    
    ITtsModule ITextToSpeechHost.Tts => TtsVm.Tts;
    IReadOnlyList<IInputStrip> ITextToSpeechHost.Inputs => TtsVm.Inputs;
    
    public TextToSpeechSettings TtsSettings => TtsVm.TtsSettings;

    public void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true) =>
        TtsVm.ApplyTtsSettings(updated, saveNow);

    public void RestoreTts() => _coordinator.RestoreTts();

    // =========================================================================
    // Импорт и экспорт настроек
    // =========================================================================
    public SettingsExportResult ExportSettings(string path) => _coordinator.ExportSettings(path);

    public SettingsImportResult ImportSettings(string path)
    {
        var result = _coordinator.ImportSettings(path);
        SyncBehaviourFlags();
        return result;
    }

    // =========================================================================
    // Интеграция с Twitch
    // =========================================================================
    private TwitchViewModel TwitchVm => _coordinator.Twitch;

    public TwitchViewModel TwitchModule => TwitchVm;
    public TwitchService Twitch => TwitchVm.Twitch;
    public bool IsTwitchConfigured => TwitchVm.IsTwitchConfigured;
    public TwitchSettings TwitchSettings => TwitchVm.TwitchSettings;

    public Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default) =>
        TwitchVm.BeginTwitchLoginAsync(cancellationToken);

    public Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge, CancellationToken cancellationToken = default) =>
        TwitchVm.ContinueTwitchLoginAsync(challenge, cancellationToken);

    public Task LogoutTwitchAsync(CancellationToken cancellationToken = default) =>
        TwitchVm.LogoutTwitchAsync(cancellationToken);

    public void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true) =>
        TwitchVm.ApplyTwitchSettings(updated, saveNow);

    public Task RestoreTwitchAsync() => TwitchVm.RestoreTwitchAsync();
    public void OpenTwitchConsole() => TwitchVm.OpenTwitchConsole();
    public void OpenTwitchActivation(string verificationUri) => TwitchVm.OpenTwitchActivation(verificationUri);

    // =========================================================================
    // Проверка обновлений
    // =========================================================================
    public UpdateViewModel Update => _updateVm;
    public UpdateInfo? PendingUpdate => _updateVm.PendingUpdate;

    public bool IsUpdateAvailable
    {
        get => _updateVm.IsUpdateAvailable;
        set
        {
            if (_updateVm.IsUpdateAvailable != value)
                _updateVm.IsUpdateAvailable = value;
        }
    }

    public string UpdateBanner
    {
        get => _updateVm.UpdateBanner;
        set
        {
            if (_updateVm.UpdateBanner != value)
                _updateVm.UpdateBanner = value;
        }
    }

    public string UpdateStatus
    {
        get => _updateVm.UpdateStatus;
        set
        {
            if (_updateVm.UpdateStatus != value)
                _updateVm.UpdateStatus = value;
        }
    }

    public Task CheckUpdatesOnStartupAsync() => _updateVm.CheckUpdatesOnStartupAsync();
    public Task CheckUpdatesAsync() => _updateVm.CheckUpdatesAsync();
    public void DismissUpdate() => _updateVm.DismissUpdateCommand.Execute(null);
}