using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;

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