using System.Reflection;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Twitch.Entities;
using Xunit;

namespace SoundMeeter.Core.Tests;

/// <summary>
/// Границы модуля Twitch (SM-F01).
///
/// Проверяем то, что компилятор не проверяет, а ломается тихо. Модуль разделён
/// на контракт, реализацию, сущности и сценарии, и каждое из этих делений
/// держится только соглашением: связь между слоями не мешает ни сборке, ни
/// функциональным проверкам — она просто делает слой бесполезным.
///
/// О ЧЕМ ИМЕННО ЭТИ ПРОВЕРКИ. Смотрится не список <c>ProjectReference</c> в
/// .csproj, а реальные ссылки собранной сборки. MSBuild не включает ссылку на
/// сборку, из которой код ничего не использует, поэтому проверка ловит именно
/// ИСПОЛЬЗОВАНИЕ — настоящее сцепление слоёв, а не безобидную строку в файле
/// проекта.
///
/// Проверка «UseCases не знает реализации» — главная. Именно её отсутствие
/// стоило дороже всего: пока сценарии ссылались на <c>TwitchAuthService</c>
/// ради константы client ID, пришлось бы тащить в UseCases сборку с HTTP,
/// WebSocket и DPAPI, и проверять оркестрацию без сети стало бы невозможно.
/// </summary>
public class TwitchModuleBoundaryTests
{
    /// <summary>
    /// Сборки WPF: их присутствие означает, что слой сценариев больше не
    /// переносим в не-WPF хост.
    /// </summary>
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    /// <summary>
    /// Прямые ссылки на сборки с заданным префиксом.
    ///
    /// Префикс, а не точное имя: проверка «нет SoundMeeter.Twitch» по точному
    /// имени проходит всегда, потому что сборка называется
    /// <c>SoundMeeter.Twitch.Contract.Implementation</c>, и сравнение строк
    /// просто никогда не сойдётся. Такая проверка выглядит работающей и не
    /// ловит ничего.
    /// </summary>
    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void UseCasesDoNotDependOnTheImplementation()
    {
        // Сценарии собираются из контрактов. Реализации знает только
        // Composition Root — он их регистрирует.
        Assert.Empty(WithPrefix(typeof(TwitchService).Assembly,
            "SoundMeeter.Twitch.Contract.Implementation"));
    }

    [Fact]
    public void EntitiesAreALeafAndDependOnNothingOfTheModule()
    {
        // Entities — чистые данные. Если отсюда потянется контракт или
        // реализация, «чистые данные» перестанут ими быть и за ними уже нельзя
        // будет обращаться из мест, где внешних систем нет.
        var entities = ReferencesOf(typeof(TwitchAppConfig).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("SoundMeeter.Twitch.Contract", StringComparison.Ordinal));
    }

    [Fact]
    public void EntitiesDoNotDependOnTheApplicationOrUiModules()
    {
        var entities = ReferencesOf(typeof(TwitchAppConfig).Assembly);

        Assert.DoesNotContain("SoundMeeter", entities);
        Assert.Empty(WithPrefix(typeof(TwitchAppConfig).Assembly, "SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix(typeof(TwitchAppConfig).Assembly, "StreamerTools.Style"));
    }

    [Fact]
    public void UseCasesDoNotDependOnWpf()
    {
        var offenders = ReferencesOf(typeof(TwitchService).Assembly)
            .Intersect(WpfAssemblies)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void UseCasesDoNotDependOnTheApplicationOrUiModules()
    {
        var useCases = typeof(TwitchService).Assembly;

        Assert.DoesNotContain("SoundMeeter", ReferencesOf(useCases));
        Assert.Empty(WithPrefix(useCases, "SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix(useCases, "StreamerTools.Style"));
    }

    [Fact]
    public void TheImplementationCarriesTheExternalSystems()
    {
        // Обратная сторона границы. Если реализация перестанет ссылаться на
        // System.Net / System.Security, значит HTTP или шифрование токена уехали
        // в сценарии — и проверка выше этого уже не заметит.
        var implementation = ReferencesOf(typeof(TwitchAuthService).Assembly);

        Assert.Contains("System.Net.Http", implementation);
        Assert.Contains("System.Net.WebSockets", implementation);

        // Точное имя: ProtectedData поставляется отдельным пакетом, и обобщённое
        // «System.Security.Cryptography» проверило бы сборку, которой тут нет.
        Assert.Contains("System.Security.Cryptography.ProtectedData", implementation);
    }

    [Fact]
    public void UseCasesDoNotCallLocalizationDirectly()
    {
        // Проверяются прямые ссылки: SoundMeeter.Core.Contract тянет
        // ChangeLanguage сам (LocalizedViewModel подписан на Loc.LanguageChanged),
        // поэтому запрет по всему графу был бы невыполнимым в принципе.
        Assert.Empty(WithPrefix(typeof(TwitchService).Assembly, "SoundMeeter.ChangeLanguage"));
    }
}