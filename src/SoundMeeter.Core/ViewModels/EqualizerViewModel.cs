using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Полоса графического эквалайзера стрипа: частота задана разметкой шага октавы,
/// а пользователь правит только усиление.
///
/// Значение живёт в модели пресета, а полоса работает с ним через
/// <see cref="InputChannelModel.GetEqBand"/>/<see cref="InputChannelModel.SetEqBand"/>:
/// кривую в окне рисует один и тот же список, который читает DSP, поэтому
/// «написано» и «слышно» не могут разойтись.
/// </summary>
public sealed partial class EqBandViewModel : LocalizedViewModel
{
    private readonly InputChannelModel _model;
    private readonly Action _markDirty;

    /// <summary>Центральная частота полосы, Гц. Задана моделью, не пользователем.</summary>
    public float Frequency { get; }

    /// <summary>
    /// Номер полосы в модели. Кривая рисует набор полос как усиления по номерам,
    /// а не по частотам: частота — это только подпись, и искать по ней индекс
    /// означало бы лишний поиск на каждый кадр отрисовки.
    /// </summary>
    public int Index { get; }

    /// <summary>Подпись частоты под узлом кривой: «125», «1k», «16k».</summary>
    public string Label { get; }

    /// <summary>
    /// Усиление полосы, дБ. Диапазон совпадает с тем, что проверяет DSP и миграция
    /// схемы: иначе полоса позволяла бы записать в пресет значение, которое
    /// обработка потом срежет, а UI врал бы.
    /// </summary>
    public float Gain
    {
        get => _model.GetEqBand(Index);
        set
        {
            float limit = InputChannelModel.EqBandGainLimitDb;
            float clamped = float.IsFinite(value) ? Math.Clamp(value, -limit, limit) : 0f;

            // Повторная запись того же значения не нужна: иначе двойной щелчок по
            // уже ровной полосе зря помечал бы пресет изменённым.
            if (_model.GetEqBand(Index) == clamped)
            {
                OnPropertyChanged(nameof(Gain));
                OnPropertyChanged(nameof(Display));
                return;
            }

            _model.SetEqBand(Index, clamped);
            _markDirty();
            OnPropertyChanged(nameof(Gain));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Значение с единицей измерения: «3.0 dB», «−4.5 dB». Формат тот же, что у
/// крутилок эффектов: подпись полосы читается на кривой, а не в таблице.</summary>
    public string Display => $"{Gain.ToString("0.0", Loc.Culture)} dB";

    /// <summary>Подсказка полосы: частота, значение и напоминание про сброс.</summary>
    public string ToolTip =>
        $"{Label} Hz: {Display}  ·  {Loc.Get("Sm.Param.DoubleClickReset")}";

    /// <summary>Двойной щелчок по узлу кривой — вернуть 0 дБ.</summary>
    [RelayCommand]
    private void Reset() => Gain = 0f;

    public EqBandViewModel(int index, float frequency, InputChannelModel model, Action markDirty)
    {
        Index = index;
        _model = model;
        _markDirty = markDirty;
        Frequency = frequency;
        Label = FormatFrequency(frequency);
    }

    /// <summary>
    /// Частота подписью: килогерцы без дробной части («1k»), остальное — как
    /// есть («125»). Подписи короче подписи полной частоты, а в узле кривой
    /// места мало.
    /// </summary>
    private static string FormatFrequency(float hz) =>
        hz >= 1000f
            ? (hz / 1000f).ToString("0.#", Loc.Culture) + "k"
            : hz.ToString("0", Loc.Culture);

    /// <summary>Перечитывает подписи после смены языка (кто-то мог поменять язык).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// Пресет кривой эквалайзера в списке выбора.
///
/// Отдельный класс, а не строка, потому что элемент списка должен и название на
/// текущем языке, и действие «применить», а список элементов ComboBox не
/// умеет ни того, ни другого сам.
/// </summary>
public sealed partial class EqPresetViewModel : LocalizedViewModel
{
    private readonly EqualizerPreset _preset;
    private readonly Action _apply;

    public EqualizerPreset Preset => _preset;

    public string Name => Loc.Get(_preset.Key);

    [RelayCommand]
    private void Apply() => _apply();

    public EqPresetViewModel(EqualizerPreset preset, Action apply)
    {
        _preset = preset;
        _apply = apply;
    }

    protected override void OnLanguageChangedCore() => OnPropertyChanged(nameof(Name));

    public override string ToString() => Name;
}

/// <summary>
/// Графический эквалайзер стрипа (SM-B05): кнопка в колонке стрипа и отдельное
/// окно настроек.
///
/// Отдельный класс, а не ещё один <see cref="StripEffectViewModel"/>, потому что
/// настройки не помещаются в попап стрипа: десять полос с кривой занимают
/// несколько сотен пикселей, а попап — 272. Поэтому же <see cref="StripEffectViewModel"/>
/// умеет только крутилки в попапе, и его комментарий про «пятый эффект без
/// правок UI» на этот случай не распространяется.
/// </summary>
public sealed partial class EqualizerViewModel : LocalizedViewModel
{
    private readonly InputChannelModel _model;
    private readonly InputChannelViewModel _owner;
    private readonly Action _markDirty;

    /// <summary>Короткая подпись для кнопки в колонке стрипа.</summary>
    public string ButtonText => "EQ";

    public string Title => Loc.Get("Sm.Effect.Equalizer");

    /// <summary>Полосы кривой, по одной на полосу модели.</summary>
    public ObservableCollection<EqBandViewModel> Bands { get; } = new();

    /// <summary>
    /// Пресеты кривой для выпадающего списка. Применяются выбором: отдельная
    /// кнопка «применить» рядом со списком означала бы два шага там, где хватает
    /// одного, и по списку пришлось бы читать подсказку, чтобы понять, что он не
    /// декоративный.
    /// </summary>
    public ObservableCollection<EqPresetViewModel> Presets { get; } = new();

    /// <summary>Крутилки окна: общий makeup-gain и два среза.</summary>
    public ObservableCollection<EffectKnobViewModel> Knobs { get; } = new();

    /// <summary>Общий makeup-gain, дБ: на кривой это сдвиг целиком вверх или вниз.</summary>
    public EffectKnobViewModel Preamp { get; }

    /// <summary>Срез снизу, Гц. Нижнее положение шкалы = «выключено».</summary>
    public EffectKnobViewModel LowCut { get; }

    /// <summary>Срез сверху, Гц. Верхнее положение шкалы = «выключено».</summary>
    public EffectKnobViewModel HighCut { get; }

    /// <summary>
    /// Эквалайзер включён.
    ///
    /// Состояние живёт в стрипе, а не здесь: кнопка EQ, MIDI и это окно должны
    /// видеть одно и то же значение, поэтому ViewModel только переадресует запись
    /// владельцу — как и <see cref="StripEffectViewModel"/> у остальных эффектов.
    /// </summary>
    public bool IsEnabled
    {
        get => _owner.EqEnabled;
        set
        {
            if (_owner.EqEnabled == value) return;
            _owner.EqEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>
    /// Стрип перестал существовать (устройство подключили или отключили — движок
    /// пересоздаёт ленту на каждом осмотре каталога). Событие владельца
    /// перекладывается сюда, чтобы окно эквалайзера не знало о стрипе и держало
    /// подписку на одного ViewModel, а не на два.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>Id стрипа-владельца: по нему окно находит своё и не путает чужие.</summary>
    public string StripId => _owner.Id;

    /// <summary>Имя канала для заголовка окна: переименование стрипа видно сразу.</summary>
    public string ChannelTitle => _owner.Title;

    public string ToolTip => IsEnabled
        ? Loc.Get("Sm.Effect.ToolTipOn", ButtonText, Title)
        : Loc.Get("Sm.Effect.ToolTipOff", ButtonText, Title);

    private EqPresetViewModel? _selectedPreset;

    /// <summary>
    /// Выбранный пресет кривой. Смена выбора применяет пресет сразу, поэтому
    /// свойство пишет в модель, а не только меняет подсветку в списке.
    ///
    /// Изначально пусто: под кривой, которую пользователь вырисовал сам, нет
    /// пресета, и показывать первый в списке было бы враньём.
    /// </summary>
    public EqPresetViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            value?.ApplyCommand.Execute(null);
        }
    }

    public EqualizerViewModel(InputChannelViewModel owner, Action markDirty)
    {
        _owner = owner;
        _model = owner.Model;
        _markDirty = markDirty;

        foreach (var preset in EqualizerPresets.All)
            Presets.Add(new EqPresetViewModel(preset, () => ApplyPreset(preset)));

        ReadOnlySpan<float> frequencies = InputChannelModel.EqBandFrequencies;
        for (int i = 0; i < InputChannelModel.EqBandCount; i++)
            Bands.Add(new EqBandViewModel(i, frequencies[i], _model, markDirty));

        Preamp = Knob("Sm.Eq.Preamp", "dB", "0.0",
            -InputChannelModel.EqPreampLimitDb, InputChannelModel.EqPreampLimitDb,
            InputChannelModel.EffectDefaults.EqPreampDb,
            () => _model.EqPreampDb, v => _model.EqPreampDb = v);

        // Нижнее положение среза — «выключено», и оно же значение по умолчанию:
        // пользователь не обязан знать, что «не резать» это край шкалы, а не
        // отсутствие настройки.
        //
        // Шкала логарифмическая: размах 20…300 Гц и 3…20 кГц — это пятнадцати-
        // и шестикратное изменение, и на линейной шкале ползунок не доезжает ни
        // до верха, ни до низа.
        LowCut = Knob("Sm.Eq.LowCut", "Hz", "0",
            InputChannelModel.EqLowCutMinHz, InputChannelModel.EqLowCutMaxHz,
            InputChannelModel.EffectDefaults.EqLowCutHz,
            () => _model.EqLowCutHz, v => _model.EqLowCutHz = v,
            logarithmic: true);

        HighCut = Knob("Sm.Eq.HighCut", "Hz", "0",
            InputChannelModel.EqHighCutMinHz, InputChannelModel.EqHighCutMaxHz,
            InputChannelModel.EffectDefaults.EqHighCutHz,
            () => _model.EqHighCutHz, v => _model.EqHighCutHz = v,
            logarithmic: true);

        Knobs.Add(Preamp);
        Knobs.Add(LowCut);
        Knobs.Add(HighCut);

        // Имя канала берётся у владельца: стрип переименовывается на месте, и окно
        // эквалайзера не должно показывать снимок на момент открытия.
        _owner.PropertyChanged += OnOwnerPropertyChanged;
        _owner.Invalidated += OnOwnerInvalidated;
    }

    /// <summary>
    /// «Сбросить всё»: ровная кривая, makeup-gain 0, срезы выключены.
    /// Подтверждения здесь нет намеренно — настройку возвращают те же крутилки и
    /// полосы, а лишний вопрос поверх одного клика только раздражает.
    /// </summary>
    [RelayCommand]
    private void Flat()
    {
        foreach (var band in Bands) band.Gain = 0f;
        foreach (var knob in Knobs) knob.Value = knob.DefaultValue;
        _markDirty();
    }

    /// <summary>
    /// Накатить пресет на канал. Полосы и makeup-gain берутся из пресета, срезы
    /// остаются как были: срез ставят под конкретный канал (гул, шум), и сбрасывать
    /// его вместе с тембром — значит ломать настройку, которой не трогали.
    ///
    /// Значения перекладываются и в ViewModel: <see cref="Preamp"/> читает модель,
    /// а полосы держат подписку на неё же, поэтому без явного обновления кривая
    /// нарисовалась бы по старым значениям.
    /// </summary>
    private void ApplyPreset(EqualizerPreset preset)
    {
        preset.ApplyTo(_model);
        Preamp.Refresh();
        foreach (var band in Bands) band.Refresh();
        _markDirty();
    }

    private EffectKnobViewModel Knob(
        string nameKey,
        string unit,
        string format,
        double min,
        double max,
        float defaultValue,
        Func<float> get,
        Action<float> set,
        bool logarithmic = false) =>
        EffectKnobs.Create(nameKey, unit, format, min, max, defaultValue, get, set, _markDirty, logarithmic);

    /// <summary>
    /// События владельца. Пустое имя — соглашение WPF «перечитать всё»: так же
    /// сам <see cref="LocalizedViewModel"/> сообщает о себе базовому классу, и
    /// вместе с ним сюда приходит и смена языка.
    ///
    /// <see cref="InputChannelViewModel.EqEnabled"/> перекладывается вручную:
    /// включением эквалайзера управляют ещё MIDI и кнопка стрипа, и без этого
    /// подписывания та кнопка показывала бы состояние, которое уже изменили.
    /// </summary>
    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "":
            case nameof(InputChannelViewModel.Title):
                OnPropertyChanged(nameof(ChannelTitle));
                break;

            case nameof(InputChannelViewModel.EqEnabled):
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(ToolTip));
                break;
        }
    }

    private void OnOwnerInvalidated(object? sender, EventArgs e) => Invalidated?.Invoke(this, e);

    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(ChannelTitle));
        foreach (var band in Bands) band.Refresh();
        foreach (var knob in Knobs) knob.Refresh();
    }

    protected override void DisposeCore()
    {
        // Владелец освобождает эквалайзер в своём DisposeCore, поэтому к этому
        // моменту подписки всё ещё живы и их надо снять.
        _owner.PropertyChanged -= OnOwnerPropertyChanged;
        _owner.Invalidated -= OnOwnerInvalidated;
        foreach (var band in Bands) band.Dispose();
        foreach (var knob in Knobs) knob.Dispose();
    }
}