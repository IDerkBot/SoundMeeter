using System.Reflection;
using Xunit;

/// <summary>
/// Границы ядра обработки звука (SM-A02, SM-A09). Проверяем то, что компилятор не
/// проверяет и что ломается тихо.
///
/// WPF не должен попадать сюда ни прямо, ни по цепочке ProjectReference. Такое уже
/// случалось при разбиении на модули: LocResources нужен был приложению, лежал в
/// ChangeLanguage вместе с Loc, и тот объявил UseWPF. Audio ссылается на
/// ChangeLanguage — и WPF, объявленный в одной строке csproj, доехал до ядра
/// обработки звука. Функциональные тесты при этом были зелёными.
///
/// Остальные границы модулей и слоёв проверяет ModuleBoundaryTests в
/// SoundMeeter.App.Tests: тот проект ссылается на приложение, а значит видит
/// рядом все сборки решения — иначе Assembly.Load не найдёт ни StartUp, ни
/// SoundMeeter.Core, границы которых как раз и проверяются там.
///
/// Этот тест, кстати, ловит и обратную историю: когда ядром был сам SoundMeeter
/// (ViewModels рядом с Views), проверить «Core не тянет WPF» было нечем — ядро и
/// приложение были одной сборкой. Теперь такая проверка для SoundMeeter.Core
/// возможна, и она добавлена вместе с разделением на Core и App.
/// </summary>
public class AudioModuleBoundaryTests
{
    /// <summary>
    /// Сборки WPF: их присутствие означает, что ядро больше не переносимо в
    /// не-WPF хост и тянет PresentationFramework без надобности.
    /// </summary>
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    /// <summary>
    /// Все сборки решения, достижимые из Audio по цепочкам ссылок, с уже
    /// загруженными сборками, чтобы не грузить их дважды.
    ///
    /// Обход обязателен, и это не перестраховка. При нарушении прямые ссылки у
    /// Audio выглядели совершенно здорово: она ссылалась на ChangeLanguage, а
    /// PresentationFramework сидел в ChangeLanguage — на два уровня глубже.
    /// Проверка одного уровня проходила, пока WPF ехал в ядро обработки звука.
    /// </summary>
    private static Dictionary<string, Assembly> ReachableModules(Assembly root)
    {
        var reachable = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        reachable.Add(root.GetName().Name!, root);
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                var name = reference.Name ?? string.Empty;

                // Нужен состав проектов решения, а не всего рантайма.
                if (!name.StartsWith("SoundMeeter.", StringComparison.Ordinal)) continue;
                if (reachable.ContainsKey(name)) continue;

                try
                {
                    var assembly = Assembly.Load(reference);
                    reachable.Add(name, assembly);
                    queue.Enqueue(assembly);
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Сборки нет рядом — доехать до неё нельзя, и это повод
                    // разобраться, а не повод замолчать. Подменять пустым набором
                    // нельзя: тест прошёл бы на неполной картине.
                    throw new InvalidOperationException(
                        $"Сборка {name} объявлена ссылкой, но её нет рядом с тестами. " +
                        "Добавь ProjectReference или проверь вывод.", e);
                }
            }
        }

        return reachable;
    }

    private static Dictionary<string, Assembly> CoreGraph() =>
        ReachableModules(typeof(SoundMeeter.Audio.StripDsp).Assembly);

    private static string[] DirectReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    [Fact]
    public void CoreDoesNotDependOnWpf()
    {
        // UseWPF объявляет прямую ссылку на PresentationFramework именно той сборке,
        // где он включён. Поэтому смотрим прямые ссылки каждой сборки графа, а не
        // только Audio: иначе нарушение, спрятанное на два уровня в ChangeLanguage,
        // не видно. Именно так это и выглядело вживую.
        var offenders = CoreGraph()
            .Where(pair => DirectReferences(pair.Value).Intersect(WpfAssemblies).Any())
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void CoreDoesNotDependOnTheApplication()
    {
        Assert.DoesNotContain("SoundMeeter", CoreGraph().Keys);
    }

    [Fact]
    public void CoreDoesNotDependOnUpdate()
    {
        // Update читает модель пресета, но не наоборот. Если ядро начнёт знать про
        // обновление, то первая же проверка версии растянет WPF-окно по всей сборке.
        Assert.DoesNotContain("SoundMeeter.Update", CoreGraph().Keys);
    }

    [Fact]
    public void CoreDoesNotDependOnTrayOrStartup()
    {
        var reachable = CoreGraph().Keys;
        Assert.DoesNotContain("SoundMeeter.TrayIcon", reachable);
        Assert.DoesNotContain("SoundMeeter.StartUp", reachable);
    }
}