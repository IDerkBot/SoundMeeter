using SoundMeeter.Services;
using Xunit;

namespace SoundMeeter.ChangeLanguage.Tests;

/// <summary>
/// Строки локализации лежат в SoundMeeter.ChangeLanguage, и это главный риск
/// переноса: имя встроенного ресурса выводится из RootNamespace, и если оно
/// разъедется с тем, что ищет <see cref="Loc"/>, интерфейс молча покажет
/// ⟨Sm.Ключ⟩ вместо текста — без исключения и без следа в журнале.
/// </summary>
public class LocTests
{
    [Fact]
    public void StringsResolveFromTheAssembly()
    {
        string value = Loc.Get("Sm.Strip.Mute");

        Assert.NotEqual($"⟨Sm.Strip.Mute⟩", value);
        Assert.NotEmpty(value);
    }

    [Fact]
    public void UnknownKeyIsShownAsTheKeyItself()
    {
        // Незакрытая строка — это баг, а не пустой элемент: ключ должен быть виден.
        Assert.Equal("⟨Sm.No.Such.Key⟩", Loc.Get("Sm.No.Such.Key"));
    }

    [Fact]
    public void PlaceholdersAreSubstituted()
    {
        string value = Loc.Get("Sm.Settings.Migrated", 2, 4);

        Assert.DoesNotContain("{0}", value);
        Assert.DoesNotContain("⟨", value);
    }

    [Fact]
    public void EveryResourceFileKeyResolves()
    {
        var values = Loc.ReadAllValues();

        // Пустой словарь означал бы, что в сборку не попал ни один resx —
        // и весь интерфейс состоял бы из ключей.
        Assert.NotEmpty(values);
        Assert.All(values, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void TrayStringsExistInBothLanguages()
    {
        // Ключи трея используются из P/Invoke-меню, где нет привязки XAML:
        // отсутствующий ключ показал бы пользователю ⟨Sm.Tray.Exit⟩ в меню,
        // и заметить это можно было бы только при попытке выйти.
        string[] keys =
        [
            "Sm.Tray.Menu", "Sm.Tray.Enabled", "Sm.Tray.EnabledTip",
            "Sm.Tray.Startup", "Sm.Tray.StartupTip",
            "Sm.Tray.Open", "Sm.Tray.RunToggle", "Sm.Tray.Exit",
            "Sm.Tray.StartupFailedTitle", "Sm.Tray.StartupFailedMessage"
        ];

        foreach (string key in keys)
        {
            string value = Loc.Get(key);
            Assert.NotEqual($"⟨{key}⟩", value);
            Assert.NotEmpty(value);
        }
    }

    [Fact]
    public void SwitchingLanguageChangesTheText()
    {
        // Ключ взят из Sm.Common.*, а не Sm.Strip.*: там есть русские переводы.
        // На «MUTE» проверка была бы пустой — «MUTE» по-русски тоже «MUTE».
        const string Key = "Sm.Common.Ok";

        string original = Loc.Culture.Name;
        try
        {
            Loc.ForceLanguage(Loc.Russian);
            string russian = Loc.Get(Key);

            Loc.ForceLanguage(Loc.English);
            string english = Loc.Get(Key);

            Assert.NotEqual($"⟨{Key}⟩", russian);
            Assert.NotEqual(english, russian);
        }
        finally
        {
            // Язык — статическое состояние на весь процесс: без возврата
            // следующий тест в этом же процессе читал бы чужой язык.
            Loc.ForceLanguage(original);
        }
    }
}
