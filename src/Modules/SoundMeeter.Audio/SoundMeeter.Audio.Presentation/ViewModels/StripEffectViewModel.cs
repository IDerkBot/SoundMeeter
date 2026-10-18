using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Audio.Presentation.ViewModels;

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
    public ObservableCollection<ViewModels.EffectKnobViewModel> Knobs { get; } = new();

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