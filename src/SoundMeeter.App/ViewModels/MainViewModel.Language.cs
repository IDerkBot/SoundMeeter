using System.Collections.ObjectModel;
using SoundMeeter.ChangeLanguage.UseCases;

namespace SoundMeeter.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // Язык и локализация
    // =========================================================================
    public ObservableCollection<LanguageOptionViewModel> Languages { get; } = new();

    private void BuildLanguages()
    {
        foreach (var code in Loc.SupportedLanguages)
            Languages.Add(new LanguageOptionViewModel(this, code)
            {
                IsSelected = string.Equals(code, Loc.RequestedLanguage, StringComparison.Ordinal),
            });
    }

    public void SelectLanguage(string? code)
    {
        var normalized = Loc.SupportedLanguages.Contains(code) ? code! : Loc.FollowSystem;
        if (string.Equals(normalized, Loc.RequestedLanguage, StringComparison.Ordinal)) return;

        Loc.SetLanguage(normalized);
        MarkLanguageSelection();

        _settingsService.Settings.Language = normalized;
        _settingsService.Save();
    }

    private void MarkLanguageSelection()
    {
        foreach (var option in Languages)
            option.IsSelected = string.Equals(option.Code, Loc.RequestedLanguage, StringComparison.Ordinal);
    }
}
