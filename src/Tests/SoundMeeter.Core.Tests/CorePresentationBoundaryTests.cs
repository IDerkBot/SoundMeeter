using SoundMeeter.Framework.Presentation.ViewModels;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Границы слоя представления ядра.
///
/// 1. WPF здесь быть не должен: все типы в сборке собираются без <c>UseWPF</c>
///    намеренно. Проверка не даст включить флаг «на будущее» — представление без
///    разметки не должно тащить PresentationFramework.
///
/// 2. На приложение ссылок быть не должно: это последняя сборка перед UI, и
///    знание о MainWindow или конвертерах означало бы, что представление ядра
///    можно написать только внутри этого приложения.
///
/// 3. Состав прочих ссылок проверяется перечнем, а не запретом. Раньше здесь стояло
///    «представление не должно знать сценариев», и правило было верным — пока в
///    сборке жили только окна поверх главного: они пассивны и получают состояние
///    через контракт. Теперь здесь же главное окно, и оно сценарии вызывает: это и
///    есть работа представления. Запрет снялся бы через полгода вместе с первым
///    же честным «ну ладно, для одного окна».
///
///    Перечень заменяет запрет не из мягкости, а из строгости: пока в списке
///    ровно эти сборки, любая новая зависимость представления — осознанное
///    решение, а не побочный эффект удобства. Обратное направление проверено
///    отдельно и жёстче, в CoreLayeringBoundaryTests.
/// </summary>
public class CorePresentationBoundaryTests
{
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static readonly Type[] WindowViewModels =
        [typeof(LogViewModel), typeof(TextToSpeechSettingsViewModel), typeof(TwitchSettingsViewModel)];

    /// <summary>
    /// Сборки решения, на которые представление имеет право ссылаться.
    ///
    /// Обновляется только вместе с реальным изменением: новая строка означает, что
    /// представление начало брать чужое, и это стоит объяснить здесь.
    /// </summary>
    private static readonly string[] AllowedSoundMeeterReferences =
    [
        // Контракты и сущности ядра — то, чем ViewModel'ы описывают мир.
        "SoundMeeter.Core.Contract",
        "SoundMeeter.Core.Entities",
        // Сценарии ядра: оркестратор, поведение приложения, список стрипов.
        "SoundMeeter.Core.UseCases",
        // Миксер: сигнал для полос и устройство как источник состояния.
        "SoundMeeter.Audio.Contract",
        "SoundMeeter.Audio.Dsp",
        // Строки интерфейса и файлы настроек.
        "SoundMeeter.ChangeLanguage.UseCases",
        "SoundMeeter.Infrastructure.Settings",
        "SoundMeeter.Infrastructure.Logger",
        // Настройки приложений, показываемые в списках маршрутизации.
        "SoundMeeter.AppRouting.Entities",
        // Модули, чьи окна открывает главное окно. Именно UseCases, а не
        // Contract: окно дока применяет настройки, а окно Twitch начинает вход —
        // это не пассивные наблюдатели.
        "SoundMeeter.Docking.UseCases",
        "SoundMeeter.Midi.UseCases",
        "SoundMeeter.TextToSpeech.Contract",
        "SoundMeeter.TextToSpeech.Entities",
        "SoundMeeter.TextToSpeech.UseCases",
        "SoundMeeter.Twitch.Contract",
        "SoundMeeter.Twitch.Entities",
        "SoundMeeter.Twitch.UseCases",
        "SoundMeeter.Update.Entities",
        "SoundMeeter.Update.UseCases",
    ];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    [Fact]
    public void ThePresentationLayerDoesNotPullInWpf()
    {
        Assert.Empty(ReferencesOf(typeof(LogViewModel).Assembly).Intersect(WpfAssemblies));
    }

    [Fact]
    public void ThePresentationLayerDoesNotDependOnTheApplication()
    {
        Assert.DoesNotContain("SoundMeeter", ReferencesOf(typeof(LogViewModel).Assembly));
    }

    [Fact]
    public void ThePresentationLayerReferencesOnlyTheLayersItNeeds()
    {
        var actual = ReferencesOf(typeof(LogViewModel).Assembly)
            .Where(n => n.StartsWith("SoundMeeter.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedSoundMeeterReferences.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void EveryWindowViewModelLivesInThePresentationLayer()
    {
        // Страховка от тихого возврата: если новый оконный ViewModel появят в
        // Core.UseCases, он не попадёт ни в один из списков выше и останется
        // незамеченным.
        foreach (var type in WindowViewModels)
        {
            Assert.Equal("SoundMeeter.Core.Presentation.ViewModels", type.Namespace);
        }
    }
}