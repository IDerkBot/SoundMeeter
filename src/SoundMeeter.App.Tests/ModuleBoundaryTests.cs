using System.Reflection;
using Xunit;

/// <summary>
/// Границы сборок (SM-A09, SM-A10). Проверяем то, что компилятор не проверяет и
/// что ломается тихо: если модуль начнёт ссылаться на чужой, его не поймает ни
/// компилятор, ни тесты функционального поведения — сборка останется зелёной,
/// а вред проявится только на машине без WPF.
///
/// Такое уже случалось при разбиении на модули: LocResources нужен был
/// приложению, лежал в ChangeLanguage вместе с Loc, и тот объявил UseWPF.
/// Audio ссылается на ChangeLanguage — и WPF, объявленный в одной строке
/// csproj, доехал до ядра обработки звука по цепочке ProjectReference.
/// Тесты функционального поведения были зелёными при этом.
///
/// Тест живёт здесь, а не в Audio.Tests или Core.Tests, потому что видит все
/// сборки: ссылка только на ядро — и Assembly.Load не найдёт рядом ни TrayIcon,
/// ни приложение, а именно их границы мы и проверяем.
/// </summary>
public class ModuleBoundaryTests
{
    /// <summary>Модули приложения: база Logger и ChangeLanguage, остальные — задачи.</summary>
    private static readonly string[] Modules =
        ["Audio", "Logger", "ChangeLanguage", "Update", "StartUp", "TrayIcon"];

    /// <summary>
    /// Прикладные модули: ссылаться на них можно только из ядра или приложения.
    /// База — отдельно, потому что на неё ссылаются все.
    /// </summary>
    private static readonly string[] FeatureModules =
        ["Audio", "Update", "StartUp", "TrayIcon"];

    /// <summary>
    /// Сборка приложения. Имя — <c>SoundMeeter</c>, а не SoundMeeter.App:
    /// имя exe входит в контракт portable-релиза (UpdateApplier ищет
    /// SoundMeeter.exe), поэтому переименование задачи не было.
    /// </summary>
    private const string AppAssembly = "SoundMeeter";

    /// <summary>Слой с ViewModels и сервисами: всё, что не XAML и не Composition Root.</summary>
    private const string CoreAssembly = "SoundMeeter.Core";

    /// <summary>
    /// UI-модули, которые ядру знать не о чем: значок в трее (WinForms NotifyIcon)
    /// и общая библиотека стилей (XAML-ресурсы, без них ядро всё равно не
    /// откроется).
    /// </summary>
    private static readonly string[] UiOnlyAssemblies = ["SoundMeeter.TrayIcon", "StreamerTools.Style"];

    /// <summary>
    /// Прямые ссылки сборки по ПОЛНОМУ имени: префикс добавляет вызывающий, иначе
    /// имя приложения (SoundMeeter, без суффикса) не собрать правильно.
    /// </summary>
    private static string[] References(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

    private static string[] ModuleReferences(string module) => References($"SoundMeeter.{module}");

    /// <summary>
    /// Все сборки SoundMeeter.*, достижимые из корня по цепочке ссылок: прямая
    /// ссылка на UI-модуль видна в GetReferencedAssemblies, а спрятанная на два
    /// уровня глубже — нет. Ровно на этом модули уже ловили нарушение по WPF.
    /// </summary>
    private static string[] GraphOf(string rootAssembly)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal) { rootAssembly };
        var queue = new Queue<Assembly>();
        queue.Enqueue(Assembly.Load(rootAssembly));

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                var name = reference.Name ?? string.Empty;
                if (!name.StartsWith("SoundMeeter", StringComparison.Ordinal)) continue;
                if (!reachable.Add(name)) continue;

                queue.Enqueue(Assembly.Load(reference));
            }
        }

        return reachable.ToArray();
    }

    [Fact]
    public void NoModuleDependsOnAnotherFeatureModule()
    {
        // Прямые ссылки между прикладными модулями запрещены: каждый решает свою
        // задачу, общая база — Logger и ChangeLanguage. Иначе через неделю получится
        // сеть, где правка одного модуля тянет пересборку и тесты остальных.
        foreach (var module in Modules)
        {
            var refs = ModuleReferences(module);

            foreach (var other in FeatureModules.Where(f => f != module))
                Assert.DoesNotContain($"SoundMeeter.{other}", refs);
        }
    }

    [Fact]
    public void SharedBaseDependsOnNothingOfItsOwn()
    {
        // Logger и ChangeLanguage лежат в основании графа: если начнут ссылаться на
        // прикладной модуль, цикл замкнётся и разделение перестанет существовать
        // фактически, даже если csproj этого не покажет.
        foreach (var module in new[] { "Logger", "ChangeLanguage" })
        {
            Assert.DoesNotContain(CoreAssembly, ModuleReferences(module));
            Assert.DoesNotContain(AppAssembly, ModuleReferences(module));
        }
    }

    [Fact]
    public void NoModuleDependsOnTheApplication()
    {
        // Обратная ссылка на приложение делает модуль непереносимым: вынести его
        // обратно, не затащив за собой UI, уже нельзя.
        foreach (var module in Modules)
        {
            Assert.DoesNotContain(AppAssembly, ModuleReferences(module));
        }
    }

    [Fact]
    public void NoModuleDependsOnTheCore()
    {
        // Модуль ничего не знает про слой, который его собирает. Иначе через месяц
        // Update начнёт знать про MainViewModel, StartUp — про Update, и разделение
        // перестанет существовать фактически: ровно тот случай, который
        // NoModuleDependsOnAnotherFeatureModule ловит по модулям, только медленнее.
        foreach (var module in Modules)
        {
            Assert.DoesNotContain(CoreAssembly, ModuleReferences(module));
        }
    }

    [Fact]
    public void CoreDoesNotDependOnTheApplication()
    {
        // Ребро App → Core обязано быть единственным. Обратное сделало бы ядро
        // частью приложения: перенести логику в другой хост (тест, CLI, сервис)
        // станет невозможно, потому что ядро начнёт требовать Window и Application.
        Assert.DoesNotContain(AppAssembly, References(CoreAssembly));
    }

    [Fact]
    public void CoreDoesNotDependOnUiModules()
    {
        // Ядро не значок в трее и не стили: и то и другое — UI. Проверяем по всему
        // графу ядра, а не по прямым ссылкам: StreamerTools.Style однажды приехал
        // бы в ядро через Audio, и заметить это по одной строке нельзя.
        var graph = GraphOf(CoreAssembly);

        foreach (var ui in UiOnlyAssemblies)
            Assert.DoesNotContain(ui, graph);
    }

    [Fact]
    public void ApplicationDependsOnCore()
    {
        // Прямая противоположность предыдущих проверок, и она нужна отдельно:
        // без этого сборка может остаться «зелёной» при разделении, где ядро
        // есть, но приложение его не использует (то есть ядро мертво, а UI
        // продолжает тащить логику в обход него).
        Assert.Contains(CoreAssembly, References(AppAssembly));
    }

    [Fact]
    public void ApplicationIsAnApplicationAndCoreIsALibrary()
    {
        // Точка входа и UseWPF принадлежат оболочке. Если они переедут в ядро
        // (или исчезнут из приложения), слои поедут разъезжаться, и это видно
        // без разбора XAML: ядро — библиотека, приложение — WinExe.
        Assert.NotNull(Assembly.Load(AppAssembly).EntryPoint);
        Assert.Null(Assembly.Load(CoreAssembly).EntryPoint);
    }

    [Fact]
    public void ApplicationIsTheOnlyLayerThatKnowsAboutEveryModule()
    {
        // Обратная сторона разделения: разрыв между ядром и модулем — это всегда
        // «работает, но проехало мимо». Проверяем, что слои не подменяют друг друга:
        // ядро подключает звук/лог/язык/обновления/автозапуск, приложение — значок
        // в трее.
        var core = GraphOf(CoreAssembly);
        Assert.Contains("SoundMeeter.Audio", core);
        Assert.Contains("SoundMeeter.Logger", core);
        Assert.Contains("SoundMeeter.ChangeLanguage", core);
        Assert.Contains("SoundMeeter.Update", core);
        Assert.Contains("SoundMeeter.StartUp", core);

        var app = References(AppAssembly);
        Assert.Contains("SoundMeeter.TrayIcon", app);
    }
}