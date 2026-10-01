using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Входной стрип (микрофон/loopback): громкость, мут, моно, соло, VU-метр
/// и два списка выходов — аппаратные (OUT, для прослушивания) и виртуальные (VIRT).
/// </summary>
public partial class InputChannelViewModel : LocalizedViewModel
{
    private readonly IAudioEngine _engine;
    private readonly Action _markDirty;
    private string _renameOriginal = "";

    /// <summary>
    /// true, пока роутинг стрипа меняет сама кнопка FUNC (применение правила
    /// или возврат к базе). Отличает эти изменения от пользовательской правки в
    /// попапах OUT/VIRT: пользовательская правка снимает нажатую кнопку, а
    /// движение самой кнопки — нет, иначе она снимала бы сама себя.
    /// </summary>
    private bool _routingByFunc;

    public InputChannelModel Model { get; }
    public string Id => Model.Id;
    public bool IsMicrophone => Model.IsMicrophone;

    /// <summary>
    /// Стрип принимает перенос приложений, только если у него есть render-устройство,
    /// которое снимает микшер: loopback-источник или вход виртуального кабеля.
    /// У микрофона и неназначенного входа принимать нечего — их назначение задаёт
    /// пользователь, и молча подменять его чужим устройством нельзя.
    /// </summary>
    public bool CanAcceptApps => AppSourceDeviceId != null;

    /// <summary>
    /// Render-устройство, в которое уходят приложения стрипа. У стрипа на входе
    /// виртуального кабеля это связанный выход того же кабеля (звук приходит в
    /// вход, а уходит в выход) либо явно выбранный пользователем, у loopback-
    /// стрипа — его собственное устройство, у микрофона — null.
    /// </summary>
    public string? AppSourceDeviceId { get; }

    /// <summary>true — стрип снимает вход виртуального кабеля. Связку кабеля
    /// движок обычно определяет сам, но имена кабелей задаёт пользователь, поэтому
    /// для них оставлен ручной выбор.</summary>
    public bool IsCableCapture { get; }

    /// <summary>true — для этого стрипа имеет смысл указать выход вручную.</summary>
    public bool CanChooseAppTarget => IsCableCapture;

    /// <summary>
    /// Куда уходят приложения (или приглашение выбрать, если цели нет). Подпись
    /// короткая: полоса стрипа 126 px, полное «Приложения → …» в неё не влезает,
    /// смысл раскрыт во всплывающей подсказке.
    /// </summary>
    public string AppTargetText => CanChooseAppTarget
        ? $"▸ {(AppSourceDeviceId != null ? AppTargetName : Loc.Get("Sm.Strip.AppTargetChoose"))} ▾"
        : "";

    /// <summary>Имя устройства, куда уходят приложения, для подписи в списке.</summary>
    public string AppTargetName { get; }

    public string AppDropHint => CanAcceptApps
        ? IsCableCapture
            ? Loc.Get("Sm.Strip.DropHint.Cable")
            : Loc.Get("Sm.Strip.DropHint.Any")
        : IsCableCapture
            ? Loc.Get("Sm.Strip.DropHint.CableNoPeer")
            : IsMicrophone
                ? Loc.Get("Sm.Strip.DropHint.Microphone")
                : Loc.Get("Sm.Strip.DropHint.NoSource");

    /// <summary>
    /// Объяснение отказа в переносе: приложение уходит в устройство, которое
    /// снимает стрип, а у этого стрипа такого устройства нет.
    /// </summary>
    public string AppRejectReason() => IsCableCapture
        ? Loc.Get("Sm.Strip.Reject.Cable", Model.Name)
        : IsMicrophone
            ? Loc.Get("Sm.Strip.Reject.Microphone", Title)
            : Loc.Get("Sm.Strip.Reject.NoSource", Title);

    [ObservableProperty]
    private string _name;

    /// <summary>Своё имя канала. Пустое — показывается имя устройства (Name).</summary>
    [ObservableProperty]
    private string _channelName;

    /// <summary>true — идёт редактирование имени (inline TextBox вверху стрипа).</summary>
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Имя, отображаемое сверху стрипа (ChannelName или имя устройства).</summary>
    public string Title => string.IsNullOrWhiteSpace(ChannelName) ? Name : ChannelName;

    public void BeginRename()
    {
        _renameOriginal = ChannelName;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        ChannelName = ChannelName?.Trim() ?? "";
        IsRenaming = false;
    }

    public void CancelRename()
    {
        ChannelName = _renameOriginal ?? "";
        IsRenaming = false;
    }

    [ObservableProperty]
    private float _volumeDb;

    /// <summary>Входное усиление канала, дБ. Диапазон и шаг задаёт крутилка.</summary>
    [ObservableProperty]
    private float _gainDb;

    /// <summary>true — на стрипе микрофона вместо списка приложений показываем
    /// крутилку усиления: приложения в микрофон не уходят, а тянуть тихий сигнал
    /// вверх без неё нечем.</summary>
    public bool ShowGainKnob => IsMicrophone && !IsCableCapture;

    /// <summary>true — стрип показывает список приложений канала.</summary>
    public bool ShowAppList => !ShowGainKnob;


    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isMono;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private float _peakLevel;

    [ObservableProperty]
    private bool _denoiserEnabled;

    [ObservableProperty]
    private float _denoiserNoiseRemover;

    [ObservableProperty]
    private float _denoiserDryWet;

    [ObservableProperty]
    private float _denoiserFormantLowDb;

    [ObservableProperty]
    private float _denoiserFormantMidDb;

    [ObservableProperty]
    private float _denoiserFormantHighDb;

    [ObservableProperty]
    private float _denoiserFormantGroupDb;

    #region Эффекты стрипа (SM-B05)

    [ObservableProperty]
    private bool _compressorEnabled;

    [ObservableProperty]
    private bool _fxGainEnabled;

    [ObservableProperty]
    private bool _delayEnabled;

    [ObservableProperty]
    private bool _reverbEnabled;

    /// <summary>Компрессор: порог, сжатие, атака, отпускание, makeup.</summary>
    public StripEffectViewModel Compressor { get; private set; } = null!;

    /// <summary>Уровень после компрессора (trim перед задержкой/реверберацией).</summary>
    public StripEffectViewModel FxGain { get; private set; } = null!;

    /// <summary>Задержка: время, повторы, гашение верхов, Wet.</summary>
    public StripEffectViewModel Delay { get; private set; } = null!;

    /// <summary>Реверберация: размер, затухание, Wet.</summary>
    public StripEffectViewModel Reverb { get; private set; } = null!;

    partial void OnCompressorEnabledChanged(bool value)
    {
        Model.CompressorEnabled = value;
        _markDirty();
    }

    partial void OnFxGainEnabledChanged(bool value)
    {
        Model.FxGainEnabled = value;
        _markDirty();
    }

    partial void OnDelayEnabledChanged(bool value)
    {
        Model.DelayEnabled = value;
        _markDirty();
    }

    partial void OnReverbEnabledChanged(bool value)
    {
        Model.ReverbEnabled = value;
        _markDirty();
    }

    private void CreateEffects()
    {
        Compressor = new StripEffectViewModel(
            () => CompressorEnabled,
            value => CompressorEnabled = value)
        {
            TitleKey = "Sm.Effect.Compressor",
            ButtonText = "CMP"
        };
        Compressor.Knobs.Add(Knob("Sm.Effect.Threshold", "dB", "0.0", -60, 0, InputChannelModel.EffectDefaults.CompressorThresholdDb, () => Model.CompressorThresholdDb, v => Model.CompressorThresholdDb = v));
        Compressor.Knobs.Add(Knob("Sm.Effect.Ratio", ":1", "0.0", 1, 20, InputChannelModel.EffectDefaults.CompressorRatio, () => Model.CompressorRatio, v => Model.CompressorRatio = v));
        Compressor.Knobs.Add(Knob("Sm.Effect.Attack", "ms", "0.0", 0.1, 100, InputChannelModel.EffectDefaults.CompressorAttackMs, () => Model.CompressorAttackMs, v => Model.CompressorAttackMs = v));
        Compressor.Knobs.Add(Knob("Sm.Effect.Release", "ms", "0", 10, 1000, InputChannelModel.EffectDefaults.CompressorReleaseMs, () => Model.CompressorReleaseMs, v => Model.CompressorReleaseMs = v));
        Compressor.Knobs.Add(Knob("Sm.Effect.Makeup", "dB", "0.0", -12, 24, InputChannelModel.EffectDefaults.CompressorMakeupDb, () => Model.CompressorMakeupDb, v => Model.CompressorMakeupDb = v));

        FxGain = new StripEffectViewModel(
            () => FxGainEnabled,
            value => FxGainEnabled = value)
        {
            TitleKey = "Sm.Effect.Gain",
            ButtonText = "GN"
        };
        FxGain.Knobs.Add(Knob("Sm.Effect.Level", "dB", "0.0", -60, 24, InputChannelModel.EffectDefaults.FxGainDb, () => Model.FxGainDb, v => Model.FxGainDb = v));

        Delay = new StripEffectViewModel(
            () => DelayEnabled,
            value => DelayEnabled = value)
        {
            TitleKey = "Sm.Effect.Delay",
            ButtonText = "DLY"
        };
        Delay.Knobs.Add(Knob("Sm.Effect.Time", "ms", "0", 1, 2000, InputChannelModel.EffectDefaults.DelayTimeMs, () => Model.DelayTimeMs, v => Model.DelayTimeMs = v));
        Delay.Knobs.Add(Knob("Sm.Effect.Feedback", "%", "0", 0, 90, InputChannelModel.EffectDefaults.DelayFeedback, () => Model.DelayFeedback, v => Model.DelayFeedback = v));
        Delay.Knobs.Add(Knob("Sm.Effect.Damping", "Hz", "0", 200, 18000, InputChannelModel.EffectDefaults.DelayDampingHz, () => Model.DelayDampingHz, v => Model.DelayDampingHz = v));
        Delay.Knobs.Add(Knob("Sm.Effect.Mix", "%", "0", 0, 100, InputChannelModel.EffectDefaults.DelayMix, () => Model.DelayMix, v => Model.DelayMix = v));

        Reverb = new StripEffectViewModel(
            () => ReverbEnabled,
            value => ReverbEnabled = value)
        {
            TitleKey = "Sm.Effect.Reverb",
            ButtonText = "RVB"
        };
        Reverb.Knobs.Add(Knob("Sm.Effect.Size", "%", "0", 0, 100, InputChannelModel.EffectDefaults.ReverbSize, () => Model.ReverbSize, v => Model.ReverbSize = v));
        Reverb.Knobs.Add(Knob("Sm.Effect.Damping", "%", "0", 0, 100, InputChannelModel.EffectDefaults.ReverbDamping, () => Model.ReverbDamping, v => Model.ReverbDamping = v));
        Reverb.Knobs.Add(Knob("Sm.Effect.Mix", "%", "0", 0, 100, InputChannelModel.EffectDefaults.ReverbMix, () => Model.ReverbMix, v => Model.ReverbMix = v));
    }

    /// <summary>
    /// Крутилка параметра. Диапазон здесь и в DSP один и тот же: иначе
    /// обработка срезала бы значение, а UI показывал бы его как есть.
    /// </summary>
    private EffectKnobViewModel Knob(
        string nameKey,
        string unit,
        string format,
        double min,
        double max,
        float defaultValue,
        Func<float> get,
        Action<float> set) =>
        new(get, value =>
        {
            set(value);
            _markDirty();
        })
        {
            NameKey = nameKey,
            Unit = unit,
            Format = format,
            Minimum = min,
            Maximum = max,
            DefaultValue = defaultValue
        };

    #endregion

    #region Сброс параметров двойным щелчком

    // Команды для ParamReset: дефолт каждого регулятора живёт в модели, а в
    // разметку попадает только команда — поэтому число в разметке разойтись с
    // моделью не может. Присваивание идёт через свойство ViewModel, так что
    // сброс к тому же значению пресет зря не помечает.

    /// <summary>Двойной щелчок по фейдеру стрипа — вернуть 0 дБ.</summary>
    [RelayCommand]
    private void ResetVolume() => VolumeDb = 0f;

    /// <summary>Двойной щелчок по крутилке входного усиления — вернуть 0 дБ.</summary>
    [RelayCommand]
    private void ResetGain() => GainDb = 0f;

    /// <summary>Двойной щелчок по Noise Remover — 100 % (денойзер полностью).</summary>
    [RelayCommand]
    private void ResetDenoiserNoiseRemover() => DenoiserNoiseRemover = 100f;

    /// <summary>Двойной щелчок по Dry/Wet — 100 % (обработанный сигнал).</summary>
    [RelayCommand]
    private void ResetDenoiserDryWet() => DenoiserDryWet = 100f;

    /// <summary>Двойной щелчок по пику Formant Low — 0 дБ (пик выключен).</summary>
    [RelayCommand]
    private void ResetDenoiserFormantLowDb() => DenoiserFormantLowDb = 0f;

    /// <summary>Двойной щелчок по пику Formant Medium — 0 дБ.</summary>
    [RelayCommand]
    private void ResetDenoiserFormantMidDb() => DenoiserFormantMidDb = 0f;

    /// <summary>Двойной щелчок по пику Formant High — 0 дБ.</summary>
    [RelayCommand]
    private void ResetDenoiserFormantHighDb() => DenoiserFormantHighDb = 0f;

    /// <summary>Двойной щелчок по общему makeup-gain — 0 дБ.</summary>
    [RelayCommand]
    private void ResetDenoiserFormantGroupDb() => DenoiserFormantGroupDb = 0f;

    #endregion

    public ObservableCollection<OutputOptionViewModel> HardwareOutputs { get; } = new();
    public ObservableCollection<OutputOptionViewModel> VirtualOutputs { get; } = new();

    /// <summary>
    /// Пользовательские кнопки FUNC: назначенные этой кнопке выходы применяются
    /// к роутингу стрипа по нажатию (ЛКМ), назначаются по правому клику.
    /// </summary>
    public FuncButtonViewModel Func1 { get; }

    public FuncButtonViewModel Func2 { get; }

    /// <summary>Запущенные приложения, перенаправленные на этот стрип (канал).</summary>
    public ObservableCollection<AppViewModel> AssignedApps { get; } = new();

    /// <summary>Постоянно назначенные (persistent) приложения этого стрипа (канала).</summary>
    public ObservableCollection<ConfiguredAppViewModel> ConfiguredApps { get; } = new();

    public InputChannelViewModel(
        InputChannelModel model,
        IAudioEngine engine,
        IReadOnlyList<OutputBusModel> buses,
        IReadOnlyList<DeviceInfo> catalog,
        Action markDirty)
    {
        Model = model;
        _engine = engine;
        _markDirty = markDirty;

        AppSourceDeviceId = ResolveAppSourceDeviceId(model, catalog);
        IsCableCapture = IsCableCaptureDevice(model, catalog);
        AppTargetName = NameOfDevice(catalog, AppSourceDeviceId);

        _name = model.Name;

        _channelName = model.ChannelName;
        _volumeDb = model.VolumeDb;
        _gainDb = model.GainDb;

        _isMuted = model.IsMuted;
        _isMono = model.IsMono;
        _isSolo = model.IsSolo;
        _denoiserEnabled = model.DenoiserEnabled;
        _denoiserNoiseRemover = model.DenoiserNoiseRemover;
        _denoiserDryWet = model.DenoiserDryWet;
        _denoiserFormantLowDb = model.DenoiserFormantLowDb;
        _denoiserFormantMidDb = model.DenoiserFormantMidDb;
        _denoiserFormantHighDb = model.DenoiserFormantHighDb;
        _denoiserFormantGroupDb = model.DenoiserFormantGroupDb;

        foreach (var bus in buses)
        {
            var route = model.BusRouting.GetValueOrDefault(bus.Id);
            var option = new OutputOptionViewModel(
                bus.Id,
                bus.Name,
                bus.ChannelName,
                route?.Enabled ?? false,
                route?.GainDb ?? 0f,
                OnOptionToggled,
                OnOptionGainChanged,
                OnOptionHidden);
            if (CablePairing.IsVirtualCableName(bus.Name)) VirtualOutputs.Add(option);
            else HardwareOutputs.Add(option);
        }

        Func1 = CreateFunc(0, model, buses);
        Func2 = CreateFunc(1, model, buses);
        CreateEffects();

        // Приводим к допустимому состоянию: у нажатой кнопки должно быть
        // назначение. Иначе после отключения выхода осталась бы включённая
        // кнопка, которая ничего не может включить (кнопка при этом отключена).
        FuncButtonViewModel? engagedFunc = FuncByIndex(Model.EngagedFunc);
        if (engagedFunc is not null && !engagedFunc.HasTargets)
            Model.EngagedFunc = InputChannelModel.NoFuncEngaged;

        RefreshCounts();
    }

    /// <summary>
    /// Обновляет подпись выхода во всех попапах стрипа после переименования
    /// канала на стрипе выхода: показываем имя канала, а не устройства.
    /// </summary>
    public void UpdateBusChannelName(string busId, string? channelName)
    {
        foreach (var option in HardwareOutputs)
            if (option.BusId == busId) option.SetChannelName(channelName);
        foreach (var option in VirtualOutputs)
            if (option.BusId == busId) option.SetChannelName(channelName);
        Func1.UpdateBusChannelName(busId, channelName);
        Func2.UpdateBusChannelName(busId, channelName);
    }

    public string OutButtonText => $"OUT {HardwareOutputs.Count(o => o.IsEnabled)}/{HardwareOutputs.Count} ▾";
    public string VirtButtonText => $"VIRT {VirtualOutputs.Count(o => o.IsEnabled)}/{VirtualOutputs.Count} ▾";
    public bool HasHardware => HardwareOutputs.Count > 0;
    public bool HasVirtual => VirtualOutputs.Count > 0;

    partial void OnChannelNameChanged(string value)
    {
        Model.ChannelName = value ?? "";
        OnPropertyChanged(nameof(Title));
        _markDirty();
    }

    partial void OnVolumeDbChanged(float value)
    {
        Model.VolumeDb = value;
        _markDirty();
    }

    /// <summary>
    /// Усиление читается источником на каждом пакете, поэтому меняется на лету:
    /// пересоздавать аудиопоток не нужно (как и посылка, SM-A02).
    /// </summary>
    partial void OnGainDbChanged(float value)
    {
        Model.GainDb = float.IsFinite(value) ? Math.Clamp(value, 0f, 60f) : 0f;
        if (Model.GainDb != value) GainDb = Model.GainDb;
        _markDirty();
    }


    partial void OnIsMutedChanged(bool value)
    {
        Model.IsMuted = value;
        _markDirty();
    }

    partial void OnIsMonoChanged(bool value)
    {
        Model.IsMono = value;
        _markDirty();
    }

    partial void OnIsSoloChanged(bool value)
    {
        _engine.SetInputSolo(Model.Id, value);
        _markDirty();
    }

    partial void OnDenoiserEnabledChanged(bool value)
    {
        Model.DenoiserEnabled = value;
        _markDirty();
    }

    partial void OnDenoiserNoiseRemoverChanged(float value)
    {
        Model.DenoiserNoiseRemover = value;
        _markDirty();
    }

    partial void OnDenoiserDryWetChanged(float value)
    {
        Model.DenoiserDryWet = value;
        _markDirty();
    }

    partial void OnDenoiserFormantLowDbChanged(float value)
    {
        Model.DenoiserFormantLowDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantMidDbChanged(float value)
    {
        Model.DenoiserFormantMidDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantHighDbChanged(float value)
    {
        Model.DenoiserFormantHighDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantGroupDbChanged(float value)
    {
        Model.DenoiserFormantGroupDb = value;
        _markDirty();
    }

    private void OnOptionToggled(OutputOptionViewModel option)
    {
        _engine.SetRoute(Model.Id, option.BusId, option.IsEnabled);
        _markDirty();
        RefreshCounts();
        ReleaseFuncScene();
    }

    /// <summary>
    /// Индивидуальная посылка входа в эту шину. Применяется на лету: тап читает
    /// GainDb из живого объекта маршрута, пересоздавать аудиопоток не нужно.
    /// </summary>
    private void OnOptionGainChanged(OutputOptionViewModel option, float gainDb)
    {
        _engine.SetRouteGain(Model.Id, option.BusId, gainDb);
        _markDirty();
    }


    /// <summary>
    /// Скрыть устройство из попапов OUT/VIRT: удаляет стрип шины и помечает
    /// устройство как скрытое (движок больше не будет его пересоздавать).
    /// </summary>
    private void OnOptionHidden(OutputOptionViewModel option)
    {
        _engine.RemoveBus(option.BusId);
        _markDirty();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(OutButtonText));
        OnPropertyChanged(nameof(VirtButtonText));
        OnPropertyChanged(nameof(HasHardware));
        OnPropertyChanged(nameof(HasVirtual));
    }

    /// <summary>
    /// Пересчитывает кнопки FUNC после любого изменения роутинга стрипа: в
    /// попапе обновляются точки «стрип идёт сюда» и подписи, у кнопок —
    /// нажатое состояние.
    /// </summary>
    private void RefreshFuncStates()
    {
        Func1.RefreshState();
        Func2.RefreshState();
    }

    #region Кнопки FUNC

    /// <summary>Нажата ли кнопка FUNC с таким номером слота.</summary>
    private bool IsFuncEngaged(FuncButtonViewModel func) =>
        Model.EngagedFunc == func.Index;

    /// <summary>Нажата ли какая-нибудь кнопка FUNC стрипа.</summary>
    private bool IsAnyFuncEngaged => Model.EngagedFunc != InputChannelModel.NoFuncEngaged;

    /// <summary>Кнопка по номеру слота (0 или 1); null — слот вне диапазона.</summary>
    private FuncButtonViewModel? FuncByIndex(int index) => index switch
    {
        0 => Func1,
        1 => Func2,
        _ => null
    };

    /// <summary>
    /// Нажатие и снятие кнопки FUNC. Здесь же живёт «активна может быть только
    /// одна»: включение второй сначала возвращает стрип к базовому роутингу и
    /// лишь затем применяет новое правило, иначе «дописка» считалась бы от
    /// правила предыдущей кнопки, а не от того, что было до FUNC.
    /// </summary>
    private void SetFuncEngaged(FuncButtonViewModel func, bool engaged)
    {
        if (engaged == IsFuncEngaged(func)) return;

        if (!engaged)
        {
            RestoreBaseRouting();
            SetEngagedFunc(InputChannelModel.NoFuncEngaged);
            return;
        }

        // Включение без назначения: в UI кнопка отключена, но программно сюда
        // попасть можно. Сообщаем представлению, что состояние не изменилось,
        // иначе ToggleButton остался бы нажатым при IsEngaged == false.
        if (!func.CanApply)
        {
            RefreshFuncStates();
            return;
        }

        if (IsAnyFuncEngaged) RestoreBaseRouting();
        CaptureBaseRouting();
        ApplyFunc(func);
        SetEngagedFunc(func.Index);
    }

    /// <summary>Пишет нажатую кнопку в модель и обновляет обе кнопки ленты.</summary>
    private void SetEngagedFunc(int index)
    {
        Model.EngagedFunc = index;
        _markDirty();
        RefreshFuncStates();
    }

    /// <summary>
    /// Снимок текущего роутинга — «то, что было до FUNC», к чему кнопка
    /// возвращает стрип при снятии.
    /// </summary>
    private void CaptureBaseRouting() =>
        Model.FuncBaseRouting = AllOutputs.ToDictionary(o => o.BusId, o => o.IsEnabled, StringComparer.Ordinal);

    private void RestoreBaseRouting()
    {
        var snapshot = Model.FuncBaseRouting;
        if (snapshot.Count == 0) return;

        // Возврат — это не пользовательская правка роутинга, поэтому нажатую
        // кнопку он снимать не должен.
        _routingByFunc = true;
        try
        {
            foreach (var option in AllOutputs)
                if (snapshot.TryGetValue(option.BusId, out bool enabled) && enabled != option.IsEnabled)
                    option.IsEnabled = enabled;
        }
        finally { _routingByFunc = false; }

        _markDirty();
        RefreshCounts();
        RefreshFuncStates();
    }

    /// <summary>
    /// Применяет назначение кнопки FUNC к роутингу стрипа: маршруты переключает
    /// <see cref="OutputOptionViewModel.IsEnabled"/>, оттуда же уходит
    /// <c>SetRoute</c> в движок и сохраняется пресет. Меняем только то, что
    /// отличается: лишний <c>SetRoute</c> пересобирает граф тапов.
    /// </summary>
    private void ApplyFunc(FuncButtonViewModel func)
    {
        var targets = func.SelectedBusIds;

        _routingByFunc = true;
        try
        {
            foreach (var option in AllOutputs)
            {
                bool assigned = targets.Contains(option.BusId);

                // «Только свои» — стрип уходит ровно в назначенные выходы;
                // «дописать» — свои включаются, чужие маршруты остаются как были.
                bool enable = assigned || (!func.IsExclusive && option.IsEnabled);
                if (enable != option.IsEnabled) option.IsEnabled = enable;
            }
        }
        finally { _routingByFunc = false; }

        _markDirty();
        RefreshCounts();
        RefreshFuncStates();
    }

    /// <summary>
    /// Роутинг или назначение изменили мимо кнопки FUNC — сценарий больше не в
    /// силе: снимаем кнопку, а текущий роутинг запоминаем как базу. Именно так,
    /// а не «откатываем на базу»: пользовательская правка должна остаться в силе,
    /// иначе галочка в OUT исчезла бы у него из-под курсора.
    /// </summary>
    private void ReleaseFuncScene()
    {
        if (_routingByFunc || !IsAnyFuncEngaged) return;

        CaptureBaseRouting();
        SetEngagedFunc(InputChannelModel.NoFuncEngaged);
    }

    /// <summary>Назначение кнопки изменилось (отметка выхода или режим) — это пресет.</summary>
    private void OnFuncAssignmentChanged(FuncButtonViewModel func)
    {
        _markDirty();
        ReleaseFuncScene();
    }

    /// <summary>Метка кнопки — только текст на кнопке, роутинг она не трогает.</summary>
    private void OnFuncLabelChanged(FuncButtonViewModel func) => _markDirty();

    /// <summary>Включён ли маршрут стрипа в этот выход — для точек в попапе FUNC.</summary>
    private bool IsRouteEnabled(string busId) =>
        AllOutputs.FirstOrDefault(o => string.Equals(o.BusId, busId, StringComparison.Ordinal))?.IsEnabled == true;

    /// <summary>
    /// Заводит назначение кнопки FUNC для слота. Списка в модели может не быть
    /// вовсе (пресет, записанный до появления кнопок, или битый JSON): разметка
    /// обращается к обоим слотам напрямую, поэтому пустое назначение дописываем.
    /// </summary>
    private FuncButtonViewModel CreateFunc(
        int slot,
        InputChannelModel model,
        IReadOnlyList<OutputBusModel> buses)
    {
        while (model.FuncButtons.Count <= slot) model.FuncButtons.Add(new FuncButtonModel());
        if (model.FuncButtons[slot] is null) model.FuncButtons[slot] = new FuncButtonModel();

        return new FuncButtonViewModel(
            slot + 1,
            model.FuncButtons[slot],
            buses,
            IsRouteEnabled,
            IsFuncEngaged,
            SetFuncEngaged,
            OnFuncAssignmentChanged,
            OnFuncLabelChanged);
    }

    #endregion

    private IEnumerable<OutputOptionViewModel> AllOutputs => HardwareOutputs.Concat(VirtualOutputs);

    /// <summary>
    /// Кнопки FUNC подписаны на смену языка вместе со стрипом, а стрипы
    /// пересоздаются на каждом <c>ChannelsChanged</c>: без освобождения их
    /// подписки копились бы в списке подписчиков Loc.LanguageChanged.
    /// </summary>
    protected override void DisposeCore()
    {
        Func1.Dispose();
        Func2.Dispose();
        Compressor.Dispose();
        FxGain.Dispose();
        Delay.Dispose();
        Reverb.Dispose();
    }

    /// <summary>
    /// Куда направить приложения, перенесённые на этот стрип. Половинки виртуального
    /// кабеля связны движком: вход кабеля снимаем мы, а играть приложения должны
    /// в его выход — иначе приложения уйдут в реальные колонки мимо кабеля.
    /// </summary>
    private static string? ResolveAppSourceDeviceId(
        InputChannelModel model,
        IReadOnlyList<DeviceInfo> catalog) =>
        CablePairing.GetAppRenderDeviceId(
            FindDevice(model, catalog), model.IsMicrophone, model.AppTargetDeviceId);

    /// <summary>true — устройство стрипа это вход виртуального кабеля: связку кабеля
    /// движок обычно определяет сам, но имена кабелей задаёт пользователь, поэтому
    /// для них оставлен ручной выбор.</summary>
    private static bool IsCableCaptureDevice(InputChannelModel model, IReadOnlyList<DeviceInfo> catalog) =>
        CablePairing.IsCableCapture(FindDevice(model, catalog));

    /// <summary>Имя устройства, куда уходят приложения, для подписи в списке.
    /// Хвост в скобках (имя драйвера) убираем — в панели стрипа текст обрезается,
    /// а «(Virtual Audio Cable)» пользователю ничего не говорит.</summary>
    private static string NameOfDevice(IReadOnlyList<DeviceInfo> catalog, string? deviceId)
    {
        if (deviceId == null) return "";

        var name = catalog.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? deviceId;

        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    /// <summary>Запись каталога для источника стрипа; null, если источник не назначен
    /// или устройства больше нет в системе.</summary>
    private static DeviceInfo? FindDevice(InputChannelModel model, IReadOnlyList<DeviceInfo> catalog) =>
        string.IsNullOrWhiteSpace(model.DeviceId)
            ? null
            : catalog.FirstOrDefault(d => string.Equals(d.DeviceId, model.DeviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Обновляет пик для VU-метра (вызывается из UI-таймера). Экспоненциальный спад.
    /// </summary>
    public void UpdatePeak(float raw)
    {
        float decayed = PeakLevel * 0.8f;
        float displayed = MathF.Max(raw, decayed);
        PeakLevel = displayed > 0.002f ? displayed : 0f;
    }
}