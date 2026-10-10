using SoundMeeter.Update.Contract.Implementation;
using SoundMeeter.Update.Entities;
using SoundMeeter.Update.UseCases;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Update.Tests;

/// <summary>
/// Границы модуля обновления (SM-G01).
///
/// Проверяем то, что компилятор не проверяет, а ломается тихо: связь между
/// слоями не мешает ни сборке, ни функциональным проверкам — она просто делает
/// слой бесполезным.
///
/// О ЧЕМ ИМЕННО ЭТИ ПРОВЕРКИ. Смотрится не список <c>ProjectReference</c> в
/// .csproj, а реальные ссылки собранной сборки. Разница существенна и проверена
/// на практике: MSBuild не включает ссылку на сборку, из которой код ничего не
/// использует, — объявленная, но не тронутая зависимость в метаданных не
/// появится и проверку не пройдёт. Это не слабость проверки, а её граница:
/// ловится именно ИСПОЛЬЗОВАНИЕ, то есть настоящее сцепление слоёв, а не
/// безобидная строка в файле проекта.
///
/// Модуль разделён недавно, и до этого разбиения ошибка была настоящей:
/// проверка подписи Authenticode (X509, чтение PE-файла) лежала в
/// <c>SoundMeeter.Update.Entities</c>. Entities — это чистые данные; криптография
/// там означала, что собрать «просто набор типов» без X509 было невозможно.
/// Теперь проверка ниже это фиксирует.
/// </summary>
public class UpdateModuleBoundaryTests
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
    /// Прямые ссылки на сборки с заданным префиксом. Префикс, а не точное имя:
    /// сравнение «нет SoundMeeter.Update» по точному имени никогда не сойдётся,
    /// потому что сборка называется <c>SoundMeeter.Update.Entities</c>, — и такая
    /// проверка выглядит работающей, не ловя ничего.
    /// </summary>
    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void UseCasesDoNotDependOnTheImplementation()
    {
        // Сценарии собираются из контрактов. Реализации знает только
        // Composition Root — он их регистрирует.
        Assert.Empty(WithPrefix(typeof(UpdateViewModel).Assembly,
            "SoundMeeter.Update.Contract.Implementation"));
    }

    [Fact]
    public void EntitiesAreALeafAndDependOnNothingOfTheModule()
    {
        // Сюда переехало из Entities то, что не является данными: проверка
        // подписи и DTO ответа GitHub. Проверка ниже не даст им вернуться.
        var entities = ReferencesOf(typeof(UpdateInfo).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("SoundMeeter.Update.Contract", StringComparison.Ordinal));
    }

    [Fact]
    public void EntitiesDoNotCarryCryptography()
    {
        // Прямая проверка того, что было ошибкой слоя: AuthenticodeVerifier в
        // Entities означал, что чтение PE-файла и X509 доступны оттуда.
        var entities = ReferencesOf(typeof(UpdateInfo).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("System.Security.Cryptography", StringComparison.Ordinal));
    }

    [Fact]
    public void UseCasesDoNotDependOnWpf()
    {
        var offenders = ReferencesOf(typeof(UpdateViewModel).Assembly)
            .Intersect(WpfAssemblies)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void UseCasesDoNotDependOnTheApplicationOrUiModules()
    {
        var useCases = typeof(UpdateViewModel).Assembly;

        Assert.DoesNotContain("SoundMeeter", ReferencesOf(useCases));
        Assert.Empty(WithPrefix(useCases, "SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix(useCases, "StreamerTools.Style"));
    }

    [Fact]
    public void TheImplementationCarriesTheExternalSystems()
    {
        // Обратная сторона границы. Если реализация перестанет ссылаться на
        // HTTP или криптографию, значит скачивание или проверка подписи уехали в
        // сценарии — и проверки UseCases выше этого не заметят.
        var implementation = ReferencesOf(typeof(GithubUpdateService).Assembly);

        Assert.Contains("System.Net.Http", implementation);

        // Точное имя: типы X509 живут в System.Security.Cryptography, отдельной
        // сборки System.Security.Cryptography.X509Certificates в .NET нет —
        // проверка по ней прошла бы вхолостую, никогда не сойдясь.
        Assert.Contains("System.Security.Cryptography", implementation);
    }

    [Fact]
    public void EntitiesDoNotCarryLocalizationEither()
    {
        // AuthenticodeVerifier звал Loc.Get прямо из Entities. Даже без крипто
        // это означало бы, что «чистые данные» тянут за собой подсистему
        // локализации и её сборку — то есть перестают ими быть.
        var entities = ReferencesOf(typeof(UpdateInfo).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("SoundMeeter.ChangeLanguage", StringComparison.Ordinal));
    }
}