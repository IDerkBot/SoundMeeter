using SoundMeeter.AppRouting.Contract;
using SoundMeeter.AppRouting.Contract.Implementation;
using SoundMeeter.AppRouting.Entities;
using System.Reflection;
using SoundMeeter.Audio.Entities;
using Xunit;

namespace SoundMeeter.AppRouting.Tests;

/// <summary>
/// Границы модуля перенаправления звука (SM-B02).
///
/// Модуль перенаправляет вывод приложений на другой endpoint Windows. Раньше он
/// состоял из одного файла <c>AppRouter</c> — статического класса с COM-интеропом
/// прямо в UseCases, плюс правила пейринга виртуального кабеля рядом с ним.
/// Сценариев у модуля не было вовсе, а <c>AudioService</c> вызывал статику
/// напрямую: подменить её было нечем, и проверка уходила в систему по-настоящему.
///
/// Теперь модуль разложен на контракт (IAppRouter), реализацию (COM-интероп) и
/// сущности (разбор имён устройств), и <c>AudioService</c> получает интерфейс.
///
/// О ЧЕМ ИМЕННО ЭТИ ПРОВЕРКИ. Смотрится не список ProjectReference в .csproj, а
/// реальные ссылки собранной сборки: MSBuild не включает ссылку на сборку, из
/// которой код ничего не использует. Поэтому проверка ловит именно
/// ИСПОЛЬЗОВАНИЕ — настоящее сцепление слоёв.
/// </summary>
public class AppRoutingModuleBoundaryTests
{
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void TheContractIsAPureInterface()
    {
        // Контракт — то, о чём договариваются слои. Ни одной зависимости, кроме
        // самой BCL: иначе «интерфейс» тянул бы за собой то, от чего его
        // должны были оградить.
        var references = ReferencesOf(typeof(IAppRouter).Assembly)
            .Where(n => !n.StartsWith("System.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(references);
    }

    [Fact]
    public void TheImplementationDoesNotDependOnAnythingButTheContract()
    {
        // COM-интероп изолирован в реализации. Если отсюда потянется Audio или
        // ядро, модуль перестанет быть отдельным.
        Assert.Empty(WithPrefix(typeof(AppRouter).Assembly, "SoundMeeter.Audio"));
        Assert.Empty(WithPrefix(typeof(AppRouter).Assembly, "SoundMeeter.Core"));
        Assert.Empty(WithPrefix(typeof(AppRouter).Assembly, "SoundMeeter.AppRouting.Entities"));
    }

    [Fact]
    public void EntitiesDoNotCarryTheComInterop()
    {
        // CablePairing — разбор имён устройств. Он должен оставаться чистым
        // правилом, доступным из Audio и ядра без COM-зависимостей.
        var entities = ReferencesOf(typeof(CablePairing).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal));
        Assert.Empty(WithPrefix(typeof(CablePairing).Assembly, "SoundMeeter.AppRouting.Contract"));
    }

    [Fact]
    public void NoPartOfTheModuleDependsOnWpf()
    {
        foreach (var assembly in new[]
                 {
                     typeof(IAppRouter).Assembly,
                     typeof(CablePairing).Assembly,
                     typeof(AppRouter).Assembly,
                 })
        {
            Assert.Empty(ReferencesOf(assembly).Intersect(WpfAssemblies));
        }
    }

    [Fact]
    public void NoPartOfTheModuleDependsOnTheApplication()
    {
        Assert.DoesNotContain("SoundMeeter", ReferencesOf(typeof(AppRouter).Assembly));
        Assert.DoesNotContain("SoundMeeter", ReferencesOf(typeof(CablePairing).Assembly));
    }
}