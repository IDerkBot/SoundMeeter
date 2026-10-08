using System.Reflection;
using Xunit;

/// <summary>
/// Ядро обязано оставаться переносимым в не-WPF хост (SM-A10).
///
/// Проверяем ровно то, что компилятор не проверяет: <c>UseWPF</c> в Core больше
/// нет, но WPF мог вернуться по цепочке ProjectReference — это уже случалось
/// дважды, и оба раза тихо:
///
/// 1. При выносе модулей <c>LocResources</c> нужен был приложению, лежал в
///    ChangeLanguage вместе с Loc, и тот объявил UseWPF. Audio ссылался на
///    ChangeLanguage — и WPF, объявленный в одной строке csproj, доехал до ядра
///    обработки звука. Функциональные тесты были зелёными.
/// 2. При разделении приложения на Core и App <c>UseWPF</c> остался в Core:
///    значки (BitmapImage), Application.Current.Dispatcher, DispatcherTimer и
///    ICollectionView в ViewModel'ах тянули WPF в слой логики. Этот тест —
///    результат того этапа: значки уехали в UI-конвертеры, таймер стал IUiTimer,
///    маршрутизация по UI-потоку идёт через IDispatcherService, фильтр списка
///    больше не ICollectionView, буфер обмена — IClipboardService.
///
/// Обход графа обязателен: нарушение прямыми ссылками у Audio выглядело
/// совершенно здорово (ссылка на ChangeLanguage), а PresentationFramework сидел
/// в ChangeLanguage — на два уровня глубже. Проверка одного уровня проходила.
///
/// Здесь, в отличие от AudioModuleBoundaryTests, проект сам не использует WPF:
/// если что-то в ядре снова попросит System.Windows, тест просто не соберётся —
/// и это вторая линия защиты после проверки ссылок.
/// </summary>
public class CoreWpfFreeTests
{
    /// <summary>
    /// Сборки WPF: их присутствие означает, что ядро больше не переносимо в
    /// не-WPF хост и тянет PresentationFramework без надобности.
    /// </summary>
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    /// <summary>
    /// Все сборки решения, достижимые из Core по цепочкам ссылок, с уже
    /// загруженными сборками, чтобы не грузить их дважды.
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
        ReachableModules(typeof(SoundMeeter.ViewModels.MainViewModel).Assembly);

    private static string[] DirectReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    [Fact]
    public void CoreDoesNotDependOnWpf()
    {
        // Проверяем прямые ссылки каждой сборки графа, а не только Core: иначе
        // нарушение, спрятанное на два уровня в модуле, не видно.
        var offenders = CoreGraph()
            .Where(pair => DirectReferences(pair.Value).Intersect(WpfAssemblies).Any())
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void CoreIsALibraryNotAnApplication()
    {
        // Точка входа принадлежит только приложению. Если Core однажды снова
        // станет WinExe (то есть вернётся вместе с UseWPF, app.manifest и
        // иконкой), слои поедут разъезжаться, и это видно без разбора XAML.
        //
        // Проверка «у приложения точка входа есть» живёт в SoundMeeter.App.Tests
        // (ModuleBoundaryTests): этот проект видит только ядро, и сборки
        // SoundMeeter.dll рядом с ним просто нет.
        Assert.Null(typeof(SoundMeeter.ViewModels.MainViewModel).Assembly.EntryPoint);
    }
}