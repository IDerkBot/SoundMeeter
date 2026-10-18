using System.Reflection;
using SoundMeeter.Midi.UseCases;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Границы модуля MIDI (SM-B01).
///
/// Проверяем то, что компилятор не проверяет, а ломается тихо: если слой
/// сценариев начнёт ссылаться на UI-сборку, сборка останется зелёной, а
/// разделение слоёв перестанет существовать фактически.
///
/// Такое уже случалось, причём дважды, и оба раза тихо.
///
/// Ребро было перевёрнуто. Пока обработка привязок жила во ViewModel модуля,
/// <c>SoundMeeter.Midi.UseCases</c> ссылался на <c>Midi.Presentation</c>, а тот
/// объявляет UseWPF. Через <c>SoundMeeter.Core.UseCases</c>, который ссылается
/// на оба, WPF доезжал до ядра — то есть ядро обработки звука больше нельзя было
/// перенести в не-WPF хост. Функциональные тесты при этом были зелёными.
///
/// Локализация перестала находиться. Тот же класс проверок ловит и это: если
/// <c>UseCases</c> начнёт брать <c>Loc.Get</c> напрямую вместо отдачи ключа,
/// модуль станет зависеть от ChangeLanguage, и проверка ниже это запретит.
/// </summary>
public class MidiModuleBoundaryTests
{
    /// <summary>
    /// Сборки WPF: их присутствие означает, что слой сценариев больше не
    /// переносим в не-WPF хост и тянет PresentationFramework без надобности.
    /// </summary>
    private static readonly string[] WpfAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

    /// <summary>
    /// Все сборки решения, достижимые из MIDI по цепочке ссылок.
    ///
    /// Обход обязателен, и это не перестраховка: при нарушении прямые ссылки у
    /// UseCases выглядели совершенно здорово — WPF сидел в
    /// <c>SoundMeeter.Midi.Presentation</c>, на один уровень глубже.
    /// Проверка одного уровня проходила, пока WPF ехал в ядро.
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

                if (!name.StartsWith("SoundMeeter.", StringComparison.Ordinal)) continue;
                if (reachable.ContainsKey(name)) continue;

                var assembly = Assembly.Load(reference);
                reachable.Add(name, assembly);
                queue.Enqueue(assembly);
            }
        }

        return reachable;
    }

    private static Dictionary<string, Assembly> MidiGraph() =>
        ReachableModules(typeof(MidiController).Assembly);

    private static string[] DirectReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    /// <summary>
    /// Сборки, имя которых начинается с префикса.
    ///
    /// Префикс, а не точное имя, намеренно. Проверка «нет SoundMeeter.Twitch» по
    /// точному имени проходит всегда: сборка называется
    /// <c>SoundMeeter.Twitch.UseCases</c>, и сравнение строк просто никогда не
    /// сойдётся. Такая проверка выглядит работающей и не ловит ничего — на
    /// этом уже спотыкались граничные тесты соседних модулей, поэтому здесь написано
    /// явно.
    /// </summary>
    private static string[] WithPrefix(string prefix) =>
        MidiGraph().Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void UseCasesDoNotDependOnWpf()
    {
        var offenders = MidiGraph()
            .Where(pair => DirectReferences(pair.Value).Intersect(WpfAssemblies).Any())
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void UseCasesDoNotDependOnPresentation()
    {
        // Ребро должно идти в одну сторону: Presentation → UseCases. Обратное
        // означало бы, что сценарии умеют в ViewModel'ы, а модуль — в XAML.
        Assert.Empty(WithPrefix("SoundMeeter.Midi.Presentation"));
    }

    [Fact]
    public void UseCasesDoNotDependOnTheApplication()
    {
        // Точное имя: сборка приложения называется именно SoundMeeter
        // (имя exe входит в контракт portable-релиза), суффиксов у неё нет.
        Assert.DoesNotContain("SoundMeeter", MidiGraph().Keys);
    }

    [Fact]
    public void UseCasesDoNotDependOnUiOnlyModules()
    {
        Assert.Empty(WithPrefix("SoundMeeter.TrayIcon"));
        Assert.Empty(WithPrefix("StreamerTools.Style"));
    }

    [Fact]
    public void UseCasesDoNotCallLocalizationDirectly()
    {
        // Проверяются ПРЯМЫЕ ссылки, а не весь граф — и это не послабление.
        // SoundMeeter.Core.Contract тянет ChangeLanguage сам (LocalizedViewModel
        // подписывается на Loc.LanguageChanged, SettingsMigrator зовёт Loc.Get),
        // поэтому по графу запрет был бы невыполнимым в принципе и в любой
        // момент отключался бы от реальной проверки кода модуля.
        //
        // Прямая ссылка — это ровно то, что означало бы «модуль сам берёт
        // перевод». Сценарий отдаёт ключ строки (MidiResult.MessageKey), а
        // переводит его Presentation.
        var direct = DirectReferences(typeof(MidiController).Assembly);

        Assert.DoesNotContain(direct, name => name.StartsWith("SoundMeeter.ChangeLanguage", StringComparison.Ordinal));
    }
}