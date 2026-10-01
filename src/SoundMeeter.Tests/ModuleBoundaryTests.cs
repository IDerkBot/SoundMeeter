using System.Reflection;
using Xunit;

/// <summary>
/// Границы сборок (SM-A09). Проверяем то, что компилятор не проверяет и что
/// ломается тихо: если модуль начнёт ссылаться на чужой, его не поймает ни
/// компилятор, ни тесты функционального поведения — сборка останется зелёной,
/// а вред проявится только на машине без WPF.
///
/// Такое уже случалось при разбиении на модули: LocResources нужен был
/// приложению, лежал в ChangeLanguage вместе с Loc, и тот объявил UseWPF.
/// Audio ссылается на ChangeLanguage — и WPF, объявленный в одной строке
/// csproj, доехал до ядра обработки звука по цепочке ProjectReference.
/// Тесты функционального поведения были зелёными при этом.
///
/// Тест живёт здесь, а не в Audio.Tests, потому что видит все сборки: тот проект
/// ссылается только на ядро, и Assembly.Load не найдёт рядом StartUp с TrayIcon.
/// </summary>
public class ModuleBoundaryTests
{
    /// <summary>Модули приложения: база Logger и ChangeLanguage, остальные — задачи.</summary>
    private static readonly string[] Modules =
        ["Audio", "Logger", "ChangeLanguage", "Update", "StartUp", "TrayIcon"];

    /// <summary>
    /// Прикладные модули: ссылаться на них можно только из приложения. База —
    /// отдельно, потому что на неё ссылаются все.
    /// </summary>
    private static readonly string[] FeatureModules =
        ["Audio", "Update", "StartUp", "TrayIcon"];

    private static string[] References(string module) =>
        Assembly.Load($"SoundMeeter.{module}")
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

    [Fact]
    public void NoModuleDependsOnAnotherFeatureModule()
    {
        // Прямые ссылки между прикладными модулями запрещены: каждый решает свою
        // задачу, общая база — Logger и ChangeLanguage. Иначе через неделю получится
        // сеть, где правка одного модуля тянет пересборку и тесты остальных.
        foreach (var module in Modules)
        {
            var refs = References(module);

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
            Assert.DoesNotContain("SoundMeeter", References(module));
        }
    }

    [Fact]
    public void NoModuleDependsOnTheApplication()
    {
        // Обратная ссылка на приложение делает модуль непереносимым: вынести его
        // обратно, не затащив за собой UI, уже нельзя.
        foreach (var module in Modules)
        {
            Assert.DoesNotContain("SoundMeeter", References(module));
        }
    }
}