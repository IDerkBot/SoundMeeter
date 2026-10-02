using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Пункт переключателя языка в меню настроек (SM-C07). Название языка
/// намеренно остаётся на нём самом: «Русский» в английском интерфейсе —
/// это название языка, а не надпись, которую надо переводить.
/// </summary>
public sealed partial class LanguageOptionViewModel : LocalizedViewModel
{
    public LanguageOptionViewModel(MainViewModel owner, string code)
    {
        Owner = owner;
        Code = code;
    }

    public MainViewModel Owner { get; }

    /// <summary>Код языка: <see cref="Loc.FollowSystem"/>, <see cref="Loc.English"/> или <see cref="Loc.Russian"/>.</summary>
    public string Code { get; }

    /// <summary>Отмечен ли язык текущим.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string Name => Code switch
    {
        Loc.Russian => Loc.Get("Sm.Language.Russian"),
        Loc.English => Loc.Get("Sm.Language.English"),
        _ => Loc.Get("Sm.Language.FollowSystem"),
    };

    [RelayCommand]
    private void Select() => Owner.SelectLanguage(Code);
}

/// <summary>
/// Главная VM: жизненный цикл и управление микшером.
/// MIDI, маршрутизация приложений и сохранение вынесены в тематические partial-файлы.
/// </summary>
public partial class MainViewModel : LocalizedViewModel
{
    private readonly IAudioEngine _engine;
    private readonly IUiTimer _meterTimer;

    /// <summary>
    /// Период обновления метров и снимка для дока. 33 мс — около 30 Гц: глаз
    /// не отличает, а снимок дока не должен упираться в частоту разметки.
    /// </summary>
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);

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
        IUpdateService updateService,
        IObsDockServer obsDock,
        IStartupService startup,
        IUiTimer meterTimer)
    {
        _audioService = audioService;
        _installedAppsService = installedAppsService;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _updateService = updateService;
        _engine = engine;
        _settings = settings;
        _midi = midi;
        _dock = obsDock;
        _startup = startup;
        _engine.ChannelsChanged += OnChannelsChanged;
        _engine.StateChanged += OnStateChanged;
        _midi.MessageReceived += OnMidiMessageReceived;
        SubscribeDock();

        _audioService.AudioDevicesChanged += async (s, e) => await RefreshDevicesAsync();
        _audioService.AppsChanged += async (s, e) => await RefreshAppsAsync();

        _ = InitializeAsync();

        _meterTimer = meterTimer;
        _meterTimer.Interval = MeterInterval;
        _meterTimer.Ticked += OnMeterTick;
        _meterTimer.Start();

        _saveTimer = new System.Threading.Timer(_ => SavePool(), null, 5000, 2000);

        BuildLanguages();
    }

    /// <summary>
    /// Переключатель языка в меню настроек. Отметка «текущий» обновляется и сразу
    /// после смены языка, и при возврате к «языку системы».
    /// </summary>
    public ObservableCollection<LanguageOptionViewModel> Languages { get; } = new();

    private void BuildLanguages()
    {
        foreach (var code in Loc.SupportedLanguages)
            Languages.Add(new LanguageOptionViewModel(this, code)
            {
                IsSelected = string.Equals(code, Loc.RequestedLanguage, StringComparison.Ordinal),
            });
    }

    /// <summary>
    /// Смена языка интерфейса. Применяется сразу (SM-C07): <see cref="Loc.SetLanguage"/>
    /// перезаливает словарь строк и поднимает уведомление, по которому перечитываются
    /// и уже открытые окна. Выбор сохраняется в settings.json.
    /// </summary>
    public void SelectLanguage(string? code)
    {
        var normalized = Loc.SupportedLanguages.Contains(code) ? code! : Loc.FollowSystem;
        if (string.Equals(normalized, Loc.RequestedLanguage, StringComparison.Ordinal)) return;

        Loc.SetLanguage(normalized);
        MarkLanguageSelection();

        // Язык пишется только через SettingsService: снимок движка его не знает.
        _settings.Settings.Language = normalized;
        _settings.Save();
    }

    private void MarkLanguageSelection()
    {
        foreach (var option in Languages)
            option.IsSelected = string.Equals(option.Code, Loc.RequestedLanguage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Подпись кнопки старт/стоп. Раньше её давал конвертер, но конвертер не
    /// перечитывается при смене языка — строки из ресурсов обязаны жить в VM.
    /// </summary>
    public string StartStopText => IsRunning ? Loc.Get("Sm.Toolbar.Stop") : Loc.Get("Sm.Toolbar.Start");

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

    /// <summary>
    /// Перестановка входных стрипов перетаскиванием. Порядок — часть сохранения
    /// (снимок пишет полосы в том порядке, в каком они в движке), а сами стрипы
    /// не пересоздаются, поэтому MIDI-привязки по Id канала остаются при своих.
    /// </summary>
    public void MoveInput(int fromIndex, int toIndex) => _engine.MoveInput(fromIndex, toIndex);

    /// <summary>Перестановка выходных стрипов перетаскиванием.</summary>
    public void MoveBus(int fromIndex, int toIndex) => _engine.MoveBus(fromIndex, toIndex);

    [RelayCommand]
    public void ToggleRun()
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StartStopText));

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
        _meterTimer.Ticked -= OnMeterTick;
        _dock.Stop();
        _dock.CommandReceived -= OnDockCommand;
        _engine.Stop();
        _engine.ChannelsChanged -= OnChannelsChanged;
        _engine.StateChanged -= OnStateChanged;
        _midi.MessageReceived -= OnMidiMessageReceived;
        _midi.Close();
    }

    private void OnMeterTick()
    {
        foreach (var vm in Inputs)
            vm.UpdatePeak(vm.Model.PeakLevel);
        foreach (var vm in Buses)
            vm.UpdatePeak(vm.Model.PeakLevel);

        // Тот же тик отдаёт панели дока свежие метры: снимок собирается на UI-потоке,
        // поэтому гонок с командами дока не возникает.
        PublishDockState();
    }

    private void OnChannelsChanged()
    {
        // Стрипы подписаны на Loc.LanguageChanged, а пересоздаются здесь на каждом
        // осмотре каталога: без Dispose они остались бы в списке подписчиков.
        foreach (var input in Inputs) input.Dispose();
        foreach (var bus in Buses) bus.Dispose();

        Inputs.Clear();
        foreach (var model in _engine.Inputs)
            Inputs.Add(new InputChannelViewModel(model, _engine, _engine.Buses, _engine.Catalog, MarkDirty));

        Buses.Clear();
        foreach (var model in _engine.Buses)
            Buses.Add(new OutputBusViewModel(_engine, model, MarkDirty, OnBusTitleChanged));

        HasVirtualCable = _engine.Catalog.Any(d => d.IsVirtualCable);

        Status = Loc.Get("Sm.Toolbar.StripCounts", _engine.Inputs.Count, _engine.Buses.Count);
        RefreshStripApps();
        MarkDirty();
    }

    private void OnStateChanged()
    {
        IsRunning = _engine.IsRunning;
        Status = IsRunning
            ? Loc.Get("Sm.Toolbar.EngineRunning")
            : Loc.Get("Sm.Toolbar.EngineStopped");
    }

    /// <summary>
    /// Смена языка: пересчитываем строку состояния и отметку в переключателе.
    /// Остальные строки обновляет базовый класс — уведомлением по всем свойствам.
    /// </summary>
    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(StartStopText));
        MarkLanguageSelection();
    }

    /// <summary>
    /// Главная VM получает события и из фоновых потоков (MIDI, команды дока),
    /// поэтому маршрутизирует их через диспетчер — в отличие от стрипов, где
    /// единственный внешний источник событий это сам <see cref="Loc"/>.
    /// </summary>
    protected override void RunOnUiThread(Action action)
    {
        if (_dispatcherService.HasThreadAccess) action();
        else _dispatcherService.Post(action);
    }

    protected override void DisposeCore()
    {
        foreach (var input in Inputs) input.Dispose();
        foreach (var bus in Buses) bus.Dispose();
        foreach (var option in Languages) option.Dispose();
    }

    /// <summary>
    /// Канал выхода переименован — обновляем подпись этой шины в попапах
    /// OUT/VIRT всех входных стрипов.
    /// </summary>
    private void OnBusTitleChanged(OutputBusModel bus)
    {
        foreach (var input in Inputs)
            input.UpdateBusChannelName(bus.Id, bus.ChannelName);
    }
}