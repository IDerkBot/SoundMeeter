using System.IO;
using System.Reflection;
using Xunit;

/// <summary>
/// Границы сборок модульного монолита. Проверяем то, что компилятор не проверяет и
/// что ломается тихо: если модуль начнёт ссылаться на приложение или UI-сборку,
/// сборка останется зелёной, а разделение слоёв перестанет существовать фактически.
///
/// Структура после переезда на модульный монолит:
///   SoundMeeter.App (WinExe) — Composition Root, единственная сборка с UI-хостом;
///   SoundMeeter.Core.UseCases — ядро с ViewModels, собирает модули;
///   SoundMeeter.*.{Contract,UseCases,Entities} — модули предметных областей;
///   SoundMeeter.Infrastructure.* — сервисы и реализации (Logger, Settings, Clipboard...);
///   SoundMeeter.Framework.* — утилиты.
///
/// Тест живёт здесь, а не в Core.Tests, потому что видит все сборки решения:
/// ссылка только на ядро — и Assembly.Load не найдёт рядом ни TrayIcon, ни
/// приложение, а именно их границы мы и проверяем.
/// </summary>
public class ModuleBoundaryTests
{
    /// <summary>Все модули и инфраструктура решения (имена сборок).</summary>
    private static readonly string[] AllModules =
    [
        // Модули предметных областей
        "SoundMeeter.Audio.Contract",
        "SoundMeeter.Audio.Contract.Implementation",
        "SoundMeeter.Audio.Dsp",
        "SoundMeeter.Midi.Contract",
        "SoundMeeter.Midi.Entities",
        "SoundMeeter.Midi.Contract.Implementation",
        "SoundMeeter.Midi.UseCases",
        "SoundMeeter.TextToSpeech.Contract",
        "SoundMeeter.TextToSpeech.Contract.Implementation",
        "SoundMeeter.TextToSpeech.UseCases",
        "SoundMeeter.Twitch.Contract",
        "SoundMeeter.Twitch.Contract.Implementation",
        "SoundMeeter.Twitch.UseCases",
        "SoundMeeter.Twitch.Entities",
        "SoundMeeter.Docking.Contract",
        "SoundMeeter.Docking.UseCases",
        "SoundMeeter.Docking.Entities",
        "SoundMeeter.AppRouting.Contract",
        "SoundMeeter.AppRouting.Contract.Implementation",
        "SoundMeeter.Update.Contract",
        "SoundMeeter.Update.Contract.Implementation",
        "SoundMeeter.Update.UseCases",
        "SoundMeeter.Update.Entities",
        "SoundMeeter.StartUp.Contract",
        "SoundMeeter.StartUp.Contract.Implementation",
        "SoundMeeter.StartUp.UseCases",
        "SoundMeeter.TrayIcon.Contract",
        "SoundMeeter.TrayIcon.UseCases",
        "SoundMeeter.ChangeLanguage.Contract",
        "SoundMeeter.ChangeLanguage.UseCases",
        "SoundMeeter.Core.Presentation",
        // Инфраструктура
        "SoundMeeter.Infrastructure.Logger",
        "SoundMeeter.Infrastructure.Settings",
        "SoundMeeter.Infrastructure.Clipboard",
        "SoundMeeter.Infrastructure.Dispatcher",
        "SoundMeeter.Infrastructure.UiTimer",
        // Framework
        "SoundMeeter.Framework.Utils",
        "SoundMeeter.Framework.Base",
    ];

    /// <summary>Слой с ViewModels: всё, что Composition Root собирает в приложение.</summary>
    private const string CoreAssembly = "SoundMeeter.Core.UseCases";

    /// <summary>Контракт ядра: модели данных и интерфейсы, общие для всех модулей.</summary>
    private const string CoreContract = "SoundMeeter.Core.Contract";

    /// <summary>
    /// Сборка приложения. Имя — <c>SoundMeeter</c>, а не SoundMeeter.App:
    /// имя exe входит в контракт portable-релиза (UpdateApplier ищет
    /// SoundMeeter.exe), поэтому переименование задачи не было.
    /// </summary>
    private const string AppAssembly = "SoundMeeter";

    /// <summary>
    /// UI-only сборки: значок в трее (WinForms NotifyIcon) и общая библиотека
    /// стилей. Ядро и модули о них ничего не знают — это уровень приложения.
    /// </summary>
    private static readonly string[] UiOnlyAssemblies =
    [
        "SoundMeeter.TrayIcon.UseCases",
        "StreamerTools.Style",
    ];

    private static string[] References(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

    /// <summary>
    /// Ссылки сборки, если она есть рядом с тестами. Пустые проекты-заглушки
    /// (Contract.Implementation без кода, Framework без файлов) в выход не
    /// попадают — их пропускаем, а не падаем.
    /// </summary>
    private static string[] ReferencesIfPresent(string assemblyName)
    {
        try
        {
            return References(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Все сборки SoundMeeter.*, достижимые из корня по цепочке ссылок: прямая
    /// ссылка на UI-модуль видна в GetReferencedAssemblies, а спрятанная на два
    /// уровня глубже — нет.
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
    public void NoModuleDependsOnTheApplication()
    {
        // Обратная ссылка на приложение делает модуль непереносимым: вынести его
        // обратно, не затащив за собой UI, уже нельзя.
        foreach (var module in AllModules)
            Assert.DoesNotContain(AppAssembly, ReferencesIfPresent(module));
    }

    [Fact]
    public void CoreDoesNotDependOnTheApplication()
    {
        // Ребро App → Core обязано быть единственным. Обратное сделало бы ядро
        // частью приложения: перенести логику в другой хост невозможно.
        Assert.DoesNotContain(AppAssembly, References(CoreAssembly));
    }

    [Fact]
    public void BaseDependsOnNothingOfItsOwn()
    {
        // Logger лежит в основании графа: если начнёт ссылаться на прикладной
        // модуль, цикл замкнётся и разделение перестанет существовать фактически.
        Assert.DoesNotContain(CoreAssembly, References("SoundMeeter.Infrastructure.Logger"));
        Assert.DoesNotContain(AppAssembly, References("SoundMeeter.Infrastructure.Logger"));
        Assert.DoesNotContain(CoreAssembly, References("SoundMeeter.ChangeLanguage.UseCases"));
        Assert.DoesNotContain(AppAssembly, References("SoundMeeter.ChangeLanguage.UseCases"));
    }

    [Fact]
    public void CoreDoesNotDependOnUiModules()
    {
        // Ядро не значок в трее и не стили. Проверяем по всему графу ядра:
        // UI-сборка могла бы приехать через транзитивную ссылку модуля.
        var graph = GraphOf(CoreAssembly);

        foreach (var ui in UiOnlyAssemblies)
            Assert.DoesNotContain(ui, graph);
    }

    [Fact]
    public void ApplicationDependsOnCore()
    {
        // Без этого сборка может оставаться «зелёной» при разделении, где ядро
        // есть, но приложение его не использует (ядро мертво).
        Assert.Contains(CoreAssembly, References(AppAssembly));
    }

    [Fact]
    public void ApplicationIsAnApplicationAndCoreIsALibrary()
    {
        // Точка входа и UseWPF принадлежат оболочке. Ядро — библиотека.
        Assert.NotNull(Assembly.Load(AppAssembly).EntryPoint);
        Assert.Null(Assembly.Load(CoreAssembly).EntryPoint);
        Assert.Null(Assembly.Load(CoreContract).EntryPoint);
    }

    [Fact]
    public void UiModulesAreNotVisibleToModules()
    {
        // Значок в трее — UI-модуль уровня приложения: ни один модуль и ядро не
        // должны ссылаться на него. WPF/WinForms в модулях запрещены.
        foreach (var module in AllModules)
            Assert.DoesNotContain("SoundMeeter.TrayIcon.UseCases", ReferencesIfPresent(module));
    }
}

