using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Services;
using System.Collections.ObjectModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Один параметр эффекта стрипа: подпись, диапазон и значение, которые крутилка
/// показывает и правит. Значение живёт в модели пресета, а крутилка работает с
/// ним через <c>Get</c>/<c>Set</c>: иначе пришлось бы дублировать 13 свойств в
/// ViewModel и в модели, а синхронизировать их вручную.
///
/// Диапазоны совпадают с теми, что проверяет DSP и миграция схемы, — иначе
/// крутилка позволяла бы записать в пресет значение, которое обработка потом
/// срежет, и UI врал бы.
/// </summary>
public sealed partial class EffectKnobViewModel : LocalizedViewModel
{
    private readonly Func<float> _get;
    private readonly Action<float> _set;

    /// <summary>Ключ подписи параметра в ресурсах.</summary>
    public string NameKey { get; init; } = "";

    /// <summary>Единица измерения для подписи значения (dB, ms, %, Гц).</summary>
    public string Unit { get; init; } = "";

    /// <summary>Формат значения: 0 знаков для целых процентов, 1 для дБ и мс.</summary>
    public string Format { get; init; } = "0.0";

    public double Minimum { get; init; }
    public double Maximum { get; init; }

    /// <summary>Текущее значение параметра (в единицах модели).</summary>
    public float Value
    {
        get => _get();
        set
        {
            float clamped = float.IsFinite(value) ? Math.Clamp(value, (float)Minimum, (float)Maximum) : (float)Minimum;

            // Повторная запись того же значения не нужна: иначе двойной щелчок по
            // уже сброшенному регулятору зря помечал бы пресет изменённым.
            if (_get() == clamped)
            {
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(Display));
                return;
            }

            _set(clamped);
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Значение по умолчанию: к нему сбрасывает двойной щелчок по крутилке.</summary>
    public float DefaultValue { get; init; }

    /// <summary>
    /// Крутилка logarithmic: доля параметра на равном угле дуги вместо единицы
    /// значения. Нужна частотным параметрам — у среза 3…20 кГц размах в 17 тысяч
    /// единиц, и на линейной шкале до верха не доехать, а кнопкой сброса не
    /// «покрутить».
    /// </summary>
    public bool IsLogarithmic { get; init; }

    /// <summary>Двойной щелчок по крутилке — вернуть значение по умолчанию.</summary>
    [RelayCommand]
    private void Reset() => Value = DefaultValue;

    public string Name => Loc.Get(NameKey);

    /// <summary>Значение с единицей измерения: «−18.0 dB», «250 ms», «35 %».</summary>
    public string Display
    {
        get
        {
            string number = Value.ToString(Format, Loc.Culture);
            return Unit.Length == 0 ? number : $"{number} {Unit}";
        }
    }

    /// <summary>Подсказка крутилки: значение и напоминание про сброс по двойному клику.</summary>
    public string ToolTip =>
        $"{Name}: {Display}  ·  {Loc.Get("Sm.Param.DoubleClickReset")}";

    public EffectKnobViewModel(Func<float> get, Action<float> set)
    {
        _get = get;
        _set = set;
    }

    /// <summary>Перечитывает значение после смены языка (кто-то мог поменять
    /// параметр из MIDI или из пресета).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// Эффект на входном стрипе (SM-B05): компрессор, trim, задержка, реверберация.
///
/// Один класс на все четыре эффекта, потому что различаются они только набором
/// крутилок и тем, как включение пишется в пресет. Разметка тоже одна
/// (<c>StripEffectSettingsView</c> + шаблон крутилки), так что добавление
/// пятого эффекта не затронет ни UI, ни код обработки.
///
/// Кнопка эффекта в колонке стрипа — переключатель (вкл/выкл), правый клик по
/// ней открывает попап с крутилками, как у DEN.
/// </summary>
public partial class StripEffectViewModel : LocalizedViewModel
{
    private readonly Action<bool> _setEnabled;

    /// <summary>Ключ названия эффекта в ресурсах («Компрессор», «Реверберация»…).</summary>
    public string TitleKey { get; init; } = "";

    /// <summary>Короткая подпись для кнопки в колонке стрипа (CMP, GN, DLY, RVB).</summary>
    public string ButtonText { get; init; } = "";

    /// <summary>Параметры эффекта: крутилки в попапе.</summary>
    public ObservableCollection<EffectKnobViewModel> Knobs { get; } = new();

    public string Title => Loc.Get(TitleKey);

    /// <summary>Эффект включён. Состояние живёт в стрипе (там же MIDI-биндинг).</summary>
    [ObservableProperty]
    private bool _isEnabled;

    public string ToolTip => IsEnabled
        ? Loc.Get("Sm.Effect.ToolTipOn", ButtonText, Title)
        : Loc.Get("Sm.Effect.ToolTipOff", ButtonText, Title);

    public StripEffectViewModel(Func<bool> isEnabled, Action<bool> setEnabled)
    {
        _setEnabled = setEnabled;
        _isEnabled = isEnabled();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _setEnabled(value);
        OnPropertyChanged(nameof(ToolTip));
    }

    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
        foreach (var knob in Knobs) knob.Refresh();
    }
}

/// <summary>
/// Заводит крутилку параметра эффекта со всеми её привязками к пресету.
///
/// Фабрика отдельная от <see cref="StripEffectViewModel.Knobs"/>, потому что
/// крутилки заводят не только эффекты с попапом: окно эквалайзера собирает свои
/// ползунки иначе, но диапазоны, шаг сброса и отметка «пресет изменён» должны
/// вести себя у всех одинаково.
/// </summary>
internal static class EffectKnobs
{
    public static EffectKnobViewModel Create(
        string nameKey,
        string unit,
        string format,
        double min,
        double max,
        float defaultValue,
        Func<float> get,
        Action<float> set,
        Action markDirty,
        bool logarithmic = false) =>
        new(get, value =>
        {
            set(value);
            markDirty();
        })
        {
            NameKey = nameKey,
            Unit = unit,
            Format = format,
            Minimum = min,
            Maximum = max,
            DefaultValue = defaultValue,
            IsLogarithmic = logarithmic
        };
}
