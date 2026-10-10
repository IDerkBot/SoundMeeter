using SoundMeeter.Core.Presentation.ViewModels;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Границы слоя представления ядра.
///
/// Здесь живут только ViewModel'ы окон, которые открываются поверх главного:
/// синтез речи, Twitch и журнал. Три наблюдения, каждое о настоящей ошибке,
/// а не о вкусе.
///
/// 1. Обратной ссылки на сценарии быть не должно. Окно получает состояние через
///    контракт (<c>ITextToSpeechHost</c>, <c>ITwitchHost</c>), и если бы оно
///    потянуло <c>SoundMeeter.Core.UseCases</c>, представление начало бы зависеть
///    от оркестрации — и наоборот, сценарии не смогли бы уйти в отдельный слой.
///
/// 2. WPF здесь быть не должен: <see cref="LogViewModel"/> и соседи собираются
///    без <c>UseWPF</c> специально. Проверка не даст включить флаг «на
///    будущее» — представление без разметки не должна тащить PresentationFramework.
///
/// 3. Ссылки на Twitch и синтез речи — на контракты, а не на реализации: окно
///    показывает состояние, но не управляет им.
/// </summary>
public class CorePresentationBoundaryTests
{
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static readonly Type[] WindowViewModels =
        [typeof(LogViewModel), typeof(TextToSpeechSettingsViewModel), typeof(TwitchSettingsViewModel)];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void ThePresentationLayerDoesNotDependOnTheScenarios()
    {
        Assert.Empty(WithPrefix(typeof(LogViewModel).Assembly, "SoundMeeter.Core.UseCases"));
    }

    [Fact]
    public void ThePresentationLayerDoesNotPullInWpf()
    {
        // Окна переедут сюда позже и тогда понадобится UseWPF. До тех пор флаг
        // означал бы только лишнюю зависимость от PresentationFramework.
        Assert.Empty(ReferencesOf(typeof(LogViewModel).Assembly).Intersect(WpfAssemblies));
    }

    [Fact]
    public void ThePresentationLayerDoesNotDependOnTheApplication()
    {
        Assert.DoesNotContain("SoundMeeter", ReferencesOf(typeof(LogViewModel).Assembly));
    }

    [Fact]
    public void WindowsShowStateThroughContractsRatherThanImplementations()
    {
        // Окно показывает состояние и не должно получать право им управлять.
        // Реализации Twitch и синтеза речи живут в *.UseCases — если бы окно
        // ссылалось на них, проверка бы упала.
        var references = ReferencesOf(typeof(LogViewModel).Assembly);

        Assert.DoesNotContain(references,
            n => n.StartsWith("SoundMeeter.Twitch.UseCases", StringComparison.Ordinal));
        Assert.DoesNotContain(references,
            n => n.StartsWith("SoundMeeter.TextToSpeech.Contract.Implementation", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryWindowViewModelLivesInThePresentationLayer()
    {
        // Страховка от тихого возврата: если новый оконный ViewModel появят в
        // Core.UseCases, он не попадёт ни в один из списков выше и останется
        // незамеченным. Здесь перечислены все трое — список придётся править
        // руками, и это сделает пропуск заметным.
        foreach (var type in WindowViewModels)
        {
            Assert.Equal("SoundMeeter.Core.Presentation.ViewModels", type.Namespace);
        }
    }
}