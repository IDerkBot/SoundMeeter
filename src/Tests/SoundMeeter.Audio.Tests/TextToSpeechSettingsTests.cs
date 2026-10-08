using SoundMeeter.Models;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Появление модуля синтеза речи (SM-E01) в схеме настроек.
///
/// Главная опасность этого перехода не в том, что он сломается, — а в том, что
/// старый settings.json начнёт говорить в эфир без спроса. Поэтому и проверяется
/// прежде всего одно: у файла, который писала прошлая сборка, модуль выключен.
/// </summary>
public class TextToSpeechSettingsTests
{
    [Fact]
    public void SchemaVersionGrewTogetherWithTheModule()
    {
        // Модуль появился в 7, сейчас схема на шаг впереди из-за интеграции с
        // Twitch (SM-F01). Проверка не про «модуль = 7», а про то, что версия
        // держится на текущей: смена формата обязана быть осознанным шагом.
        Assert.Equal(8, SettingsMigrator.CurrentSchemaVersion);
    }

    [Fact]
    public void AnOldFileGetsTheModuleSwitchedOff()
    {
        var settings = Migrate(new AppSettings { SchemaVersion = 6 });

        Assert.NotNull(settings.TextToSpeech);
        Assert.False(settings.TextToSpeech.Enabled);
    }

    [Fact]
    public void AnOldFileHasNothingConfiguredBecauseNothingExistedToConfigure()
    {
        var tts = Migrate(new AppSettings { SchemaVersion = 6 }).TextToSpeech;

        // Канал, голос и команды — то, что мог бы выбрать только пользователь.
        Assert.Equal("", tts.TargetInputId);
        Assert.Equal("", tts.TargetInputDeviceId);
        Assert.Equal("", tts.DefaultVoice);
        Assert.Empty(tts.UserVoices);
        Assert.Empty(tts.IgnoredUsers);
    }

    [Fact]
    public void AMigratedFileKeepsReadableDefaults()
    {
        var tts = Migrate(new AppSettings { SchemaVersion = 6 }).TextToSpeech;

        Assert.Equal("!tts", tts.SpeakCommand);
        Assert.Equal("!ttsvoice", tts.VoiceCommand);
        Assert.Equal(0, tts.Rate);
        Assert.Equal(100, tts.Volume);
        Assert.True(tts.AllowVoiceChange);
    }

    /// <summary>
    /// Ручная правка файла версии 6: блок настроек модуля в нём появиться не мог,
    /// но скопировать его из файла новой схемы — можно. Такая правка не должна ни
    /// включить модуль, ни оставить разбор команд сломанным.
    /// </summary>
    [Fact]
    public void AHandInsertedSettingsBlockCannotSwitchTheModuleOn()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 6,
            TextToSpeech = new TextToSpeechSettings
            {
                Enabled = true,
                TargetInputId = "some-strip",
                DefaultVoice = "Microsoft Irina",
                Rate = 7,
                MaxQueueLength = 9999,
            },
        };

        var tts = Migrate(settings).TextToSpeech;

        Assert.False(tts.Enabled);
        Assert.Equal("", tts.TargetInputId);
        Assert.Equal("", tts.DefaultVoice);
        Assert.Equal(0, tts.Rate);
        Assert.Equal(new TextToSpeechSettings().MaxQueueLength, tts.MaxQueueLength);
    }

    /// <summary>
    /// Списки приводятся в порядок, а не выбрасываются: пустой логин в записи о
    /// голосе и «@» в начале логина сломали бы разбор команд, а привести к виду
    /// ничего не стоит.
    /// </summary>
    [Fact]
    public void UserVoicesAreNormalizedAndDeduplicated()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 6,
            TextToSpeech = new TextToSpeechSettings
            {
                UserVoices =
                [
                    new("moderator", "Microsoft Irina"),
                    new("Moderator", "Microsoft Pavel"),   // тот же логин в другом регистре
                    new("  ", "Microsoft Zira"),           // пустой логин
                    new("moder2", "  Microsoft Pavel  "),  // лишние пробелы
                ],
            },
        };

        var voices = Migrate(settings).TextToSpeech.UserVoices;

        Assert.Equal(2, voices.Count);

        // Из двух записей одного логина остаётся последняя — она и есть актуальная.
        // Регистр логина при этом берётся из первой: сравнение всё равно идёт без
        // учёта регистра, а хранить две копии одного пользователя нельзя.
        Assert.Equal("moderator", voices[0].User, ignoreCase: true);
        Assert.Equal("Microsoft Pavel", voices[0].Voice);
        Assert.Equal("moder2", voices[1].User);
        Assert.Equal("Microsoft Pavel", voices[1].Voice);
    }

    [Fact]
    public void IgnoredUsersAreNormalizedWithoutTheAtSign()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 6,
            TextToSpeech = new TextToSpeechSettings
            {
                IgnoredUsers = ["@bot", " BOT ", "", "  ", "spammer", "Spammer"],
            },
        };

        Assert.Equal(new[] { "bot", "spammer" }, Migrate(settings).TextToSpeech.IgnoredUsers);
    }

    /// <summary>
    /// Старые стрипы не генерируемые: в прошлой сборке не было ни такого признака,
    /// ни модуля, который его ставил. Если бы миграция этого не сказала, в файле
    /// остались бы входы, которые движок вдруг счёл бы наполняемыми кодом.
    /// </summary>
    [Fact]
    public void OldStripsAreNotMarkedAsGenerated()
    {
        var settings = new AppSettings { SchemaVersion = 6 };
        settings.Inputs.Add(new InputChannelModel
        {
            Name = "MIC Микрофон",
            DeviceId = "dev-1",
            IsGenerated = true,
        });

        Assert.All(Migrate(settings).Inputs, input => Assert.False(input.IsGenerated));
    }

    [Fact]
    public void AFreshFileIsAlreadyCurrentAndMigratesWithoutChanges()
    {
        var settings = new AppSettings { SchemaVersion = SettingsMigrator.CurrentSchemaVersion };
        settings.TextToSpeech.Enabled = true;
        settings.TextToSpeech.Rate = 3;

        var outcome = SettingsMigrator.Migrate(settings, out string message);

        Assert.Equal(MigrationOutcome.Loaded, outcome);
        Assert.Equal("", message);
        Assert.True(settings.TextToSpeech.Enabled);
        Assert.Equal(3, settings.TextToSpeech.Rate);
    }

    /// <summary>Файл нулевой версии (написана сборкой до введения версионирования) доходит до текущей.</summary>
    [Fact]
    public void APresetWithoutAVersionNumberAlsoGetsTheModuleOff()
    {
        var settings = new AppSettings { SchemaVersion = 0 };
        settings.Inputs.Add(new InputChannelModel { Name = "SPK Системный звук", DeviceId = "dev-1" });

        var migrated = Migrate(settings);

        Assert.Equal(SettingsMigrator.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.False(migrated.TextToSpeech.Enabled);
        Assert.All(migrated.Inputs, input => Assert.False(input.IsGenerated));
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        SettingsMigrator.Migrate(settings, out _);
        return settings;
    }
}
