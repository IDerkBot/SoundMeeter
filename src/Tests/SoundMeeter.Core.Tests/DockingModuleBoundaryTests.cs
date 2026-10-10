using SoundMeeter.Docking.Contract.Implementation;
using SoundMeeter.Docking.Entities;
using SoundMeeter.Docking.UseCases;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Границы модуля док-панели OBS.
///
/// Модуль разделён на контракт, реализацию, сущности, сценарии и представление,
/// и каждое деление держится соглашением, а не компилятором.
///
/// Главное, что проверяет этот файл, — разрыв цикла. Раньше док жил одним
/// классом <c>DockViewModel</c> в UseCases, и ядро (<c>SoundMeeter.Core.UseCases</c>)
/// брало его в конструктор. Перенос представления в
/// <c>SoundMeeter.Docking.Presentation</c> дал бы
/// Core → Presentation → UseCases, и модуль перестал бы собираться. Сейчас ядро
/// зависит от <see cref="DockController"/> — обычного класса без UI-наследования,
/// и Presentation в обратную сторону не смотрит.
///
/// О ЧЕМ ИМЕННО ЭТИ ПРОВЕРКИ. Смотрится не список ProjectReference в .csproj, а
/// реальные ссылки собранной сборки: MSBuild не включает ссылку на сборку, из
/// которой код ничего не использует. Поэтому проверка ловит именно
/// ИСПОЛЬЗОВАНИЕ — настоящее сцепление слоёв.
/// </summary>
public class DockingModuleBoundaryTests
{
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    /// <summary>
    /// Прямые ссылки на сборки с заданным префиксом. Префикс, а не точное имя:
    /// сравнение «нет SoundMeeter.Docking» по точному имени никогда не сойдётся,
    /// потому что сборка называется <c>SoundMeeter.Docking.UseCases</c>, — и такая
    /// проверка выглядит работающей, не ловя ничего.
    /// </summary>
    private static string[] WithPrefix(Assembly assembly, string prefix) =>
        ReferencesOf(assembly).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void UseCasesDoNotDependOnThePresentation()
    {
        // То самое место, где раньше был цикл. Проверяется и по UseCases, и по
        // ядру: цикл виден с обеих сторон.
        Assert.Empty(WithPrefix(typeof(DockController).Assembly, "SoundMeeter.Docking.Presentation"));
    }

    [Fact]
    public void CoreDoesNotDependOnTheDockPresentation()
    {
        // Ядро собирает модуль дока и должно видеть только оркестрацию.
        var core = typeof(SoundMeeter.Core.UseCases.MainViewModel).Assembly;

        Assert.Empty(WithPrefix(core, "SoundMeeter.Docking.Presentation"));
        Assert.Contains("SoundMeeter.Docking.UseCases", ReferencesOf(core));
    }

    [Fact]
    public void UseCasesDoNotDependOnTheImplementation()
    {
        // Сценарии работают с контрактами; сборку реализации знает Composition Root.
        Assert.Empty(WithPrefix(typeof(DockController).Assembly,
            "SoundMeeter.Docking.Contract.Implementation"));
    }

    [Fact]
    public void UseCasesDoNotDependOnWpf()
    {
        var offenders = ReferencesOf(typeof(DockController).Assembly)
            .Intersect(WpfAssemblies)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void UseCasesDoNotDependOnTheApplicationOrUiModules()
    {
        var useCases = typeof(DockController).Assembly;

        Assert.DoesNotContain("SoundMeeter", ReferencesOf(useCases));
        Assert.Empty(WithPrefix(useCases, "SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix(useCases, "StreamerTools.Style"));
    }

    [Fact]
    public void EntitiesAreALeafAndDependOnNothingOfTheModule()
    {
        // ObsDockInstallResult переехал сюда из реализации установщика: его
        // одинаково показывают реализация, сценарии и окно.
        var entities = ReferencesOf(typeof(ObsDockInstallResult).Assembly);

        Assert.DoesNotContain(entities,
            n => n.StartsWith("SoundMeeter.Docking.Contract", StringComparison.Ordinal));
    }

    [Fact]
    public void TheImplementationCarriesTheExternalSystems()
    {
        // Обратная сторона границы: сервер держит сокеты, установщик спрашивает
        // процессы OBS, конфиг правит файл. Если эти ссылки исчезнут, значит
        // работа с внешними системами уехала в сценарии.
        var implementation = ReferencesOf(typeof(ObsDockServer).Assembly);

        Assert.Contains("System.Net.Sockets", implementation);
        Assert.Contains("System.Diagnostics.Process", implementation);
    }
}