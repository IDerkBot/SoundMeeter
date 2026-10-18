using System.Reflection;
using SoundMeeter.Logger.Presentation;
using SoundMeeter.TextToSpeech.Presentation;
using SoundMeeter.Twitch.Presentation.ViewModels;
using Xunit;

namespace SoundMeeter.Core.Tests;

/// <summary>
/// Границы слоя представления.
///
/// Проверка того, что оконные ViewModel'ы живут в Presentation-слоях своих
/// модулей, а не в едином SoundMeeter.Core.Presentation. Это страховка от
/// тихого возврата: если новый оконный ViewModel появится в UseCases, он
/// не попадёт в списки и останется незамеченным.
/// </summary>
public class CorePresentationBoundaryTests
{
    private static readonly Type[] WindowViewModels =
        [typeof(LogViewModel), typeof(TextToSpeechSettingsViewModel), typeof(TwitchSettingsViewModel)];

    [Fact]
    public void EveryWindowViewModelLivesInThePresentationLayer()
    {
        // Оконные ViewModel'ы живут в Presentation-слоях своих модулей,
        // а не в едином SoundMeeter.Core.Presentation:
        // - LogViewModel в SoundMeeter.Logger.Presentation
        // - TextToSpeechSettingsViewModel в SoundMeeter.TextToSpeech.Presentation
        // - TwitchSettingsViewModel в SoundMeeter.Twitch.Presentation.ViewModels
        Assert.Equal("SoundMeeter.Logger.Presentation", typeof(LogViewModel).Namespace);
        Assert.Equal("SoundMeeter.TextToSpeech.Presentation", typeof(TextToSpeechSettingsViewModel).Namespace);
        Assert.Equal("SoundMeeter.Twitch.Presentation.ViewModels", typeof(TwitchSettingsViewModel).Namespace);
    }
}
