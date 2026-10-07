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

    /// <summary>
    /// Строки модуля синтеза речи (SM-E01) — и в разметке, и в коде, причём
    /// половина считается в ViewModel, а не берётся из словаря. Пропавшая строка
    /// показывается как ⟨Sm.Tts.Ключ⟩, и для строки статуса это выглядит как
    /// «модуль сломан», а не как «забыли перевод».
    /// </summary>
    [Theory]
    [InlineData("Sm.Toolbar.TextToSpeech")]
    [InlineData("Sm.Toolbar.TextToSpeechTip")]
    [InlineData("Sm.Tts.Title")]
    [InlineData("Sm.Tts.Enable")]
    [InlineData("Sm.Tts.Target")]
    [InlineData("Sm.Tts.TargetHintStrip")]
    [InlineData("Sm.Tts.TargetHintGenerated")]
    [InlineData("Sm.Tts.GeneratedTarget")]
    [InlineData("Sm.Tts.NoTarget")]
    [InlineData("Sm.Tts.DefaultVoice")]
    [InlineData("Sm.Tts.NoVoicesHint")]
    [InlineData("Sm.Tts.RussianVoiceFound")]
    [InlineData("Sm.Tts.NoRussianVoice")]
    [InlineData("Sm.Tts.Rate")]
    [InlineData("Sm.Tts.Volume")]
    [InlineData("Sm.Tts.SpeakCommand")]
    [InlineData("Sm.Tts.VoiceCommand")]
    [InlineData("Sm.Tts.AllowVoiceChange")]
    [InlineData("Sm.Tts.MaxQueue")]
    [InlineData("Sm.Tts.MaxPerMinute")]
    [InlineData("Sm.Tts.MaxChars")]
    [InlineData("Sm.Tts.IgnoredUsers")]
    [InlineData("Sm.Tts.UserVoices")]
    [InlineData("Sm.Tts.UserLabel")]
    [InlineData("Sm.Tts.AddVoice")]
    [InlineData("Sm.Tts.RemoveVoice")]
    [InlineData("Sm.Tts.UserVoiceAdded")]
    [InlineData("Sm.Tts.UserVoiceRemoved")]
    [InlineData("Sm.Tts.Test")]
    [InlineData("Sm.Tts.TestHint")]
    [InlineData("Sm.Tts.TestUser")]
    [InlineData("Sm.Tts.NeedTestText")]
    [InlineData("Sm.Tts.NeedUserAndVoice")]
    [InlineData("Sm.Tts.NotACommand")]
    [InlineData("Sm.Tts.Stop")]
    [InlineData("Sm.Tts.ClearQueue")]
    [InlineData("Sm.Tts.Saved")]
    [InlineData("Sm.Tts.Queued")]
    [InlineData("Sm.Tts.EngineUnavailable")]
    [InlineData("Sm.Tts.NoVoices")]
    [InlineData("Sm.Tts.Status.Running")]
    [InlineData("Sm.Tts.Status.Off")]
    [InlineData("Sm.Tts.Status.Stopped")]
    [InlineData("Sm.Tts.Status.Queued")]
    [InlineData("Sm.Tts.Status.Speaking")]
    [InlineData("Sm.Tts.Status.VoiceAssigned")]
    [InlineData("Sm.Tts.Status.VoiceChangeOff")]
    [InlineData("Sm.Tts.Status.UnknownVoice")]
    [InlineData("Sm.Tts.Status.EmptyText")]
    [InlineData("Sm.Tts.Status.RateLimited")]
    [InlineData("Sm.Tts.Status.NoTarget")]
    [InlineData("Sm.Tts.Status.NotDelivered")]
    [InlineData("Sm.Tts.Status.NoVoices")]
    [InlineData("Sm.Tts.Status.SynthesisFailed")]
    public void TextToSpeechStringsExistInBothLanguages(string key)
    {
        // Язык — статическое состояние на весь процесс: без возврата следующий тест
        // в этом же процессе читал бы чужой язык.
        string original = Loc.Culture.Name;
        try
        {
            foreach (string code in new[] { Loc.English, Loc.Russian })
            {
                Loc.ForceLanguage(code);

                // Аргументы подставляются всегда: у части строк есть {0}, и без них
                // проверка «не осталось плейсхолдера» ловила бы их как ошибку.
                // Лишние аргументы строке без плейсхолдеров не мешают.
                string value = Loc.Get(key, "1", "2");

                Assert.NotEqual($"⟨{key}⟩", value);
                Assert.NotEmpty(value);
                Assert.DoesNotContain("{0}", value);
                Assert.DoesNotContain("{1}", value);
            }
        }
        finally
        {
            Loc.ForceLanguage(original);
        }
    }

    [Fact]
    public void SwitchingLanguageChangesTheText()    {
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
