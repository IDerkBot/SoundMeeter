using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.TextToSpeech.Entities;
using System.Reflection;
using Xunit;
using SoundMeeter.TextToSpeech.Contract.Implementation;

namespace SoundMeeter.Tests;

/// <summary>
/// Границы модуля синтеза речи (SM-E01).
///
/// Модуль — образец правильной раскладки: реализации SAPI живут в
/// <c>Contract.Implementation</c> с самого начала, а разбор команд чата и
/// доменные типы — в <c>Entities</c>. Такие проверки ставятся, чтобы правильная
/// раскладка не разъехалась: она держится договорённостью, а не компилятором.
///
/// Из недавнего: <c>TtsChatCommandParser</c> с четырьмя доменными моделями лежал
/// в <c>Contract</c> вместе с интерфейсами. Контракт — это то, о чём договариваются
/// слои, и 282 строки разбора команд туда не относились; теперь он в Entities,
/// откуда его видно и синтезатору, и ядру.
///
/// О НАЗВАНИЯХ ПРОСТРАНСТВ. До недавно Contract, Contract.Implementation и
/// UseCases этого модуля делили одно пространство <c>SoundMeeter.Services.TextToSpeech</c>,
/// доставшееся от монолита: разделение слоёв было, а в коде не читалось —
/// using не различал сборки. Теперь у каждой своё имя, как у остальных модулей,
/// и using наконец означает, ЧТО именно доступно слою.
/// </summary>
public class TextToSpeechModuleBoundaryTests
{
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void UseCasesDoNotDependOnTheImplementation()
    {
        // Сценарии работают с контрактами синтезатора и каталога голосов;
        // сборку с SAPI знает только Composition Root.
        Assert.Empty(WithPrefix(typeof(TextToSpeechService).Assembly,
            "SoundMeeter.TextToSpeech.Contract.Implementation"));
    }

    [Fact]
    public void EntitiesCarryOnlyTheDomainRules()
    {
        // Разбор команд чата — чистая функция от строки. Если отсюда потянется
        // контракт или реализация, Entities перестанет быть листом, и знать
        // «что такое команда !tts» станет невозможно без синтезатора.
        var entities = ReferencesOf(typeof(TtsChatCommand).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("SoundMeeter.TextToSpeech.Contract", StringComparison.Ordinal));
    }

    [Fact]
    public void UseCasesDoNotDependOnWpf()
    {
        Assert.Empty(ReferencesOf(typeof(TextToSpeechService).Assembly).Intersect(WpfAssemblies));
    }

    [Fact]
    public void UseCasesDoNotDependOnTheApplicationOrUiModules()
    {
        var useCases = typeof(TextToSpeechService).Assembly;

        Assert.DoesNotContain("SoundMeeter", ReferencesOf(useCases));
        Assert.Empty(WithPrefix(useCases, "SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix(useCases, "StreamerTools.Style"));
    }

    [Fact]
    public void TheImplementationCarriesSapi()
    {
        // Обратная сторона границы. Синтез речи — единственное место в solution,
        // где говорит SAPI; если ссылка на него исчезнет, значит работа уехала в
        // сценарии, и проверки выше этого не заметят.
        Assert.Contains("System.Speech", ReferencesOf(typeof(SapiSpeechEngine).Assembly));
    }
}
