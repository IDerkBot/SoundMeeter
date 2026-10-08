using SoundMeeter.Models;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Появление интеграции с Twitch (SM-F01) в схеме настроек.
///
/// Опасность перехода ровно та же, что была у модуля синтеза речи, и на этот раз
/// острее: интеграция обращается к чужому сервису и читает личные сообщения из
/// чата. Старый settings.json не может содержать её настроек, значит любой блок
/// <c>Twitch</c> в нём — мусор или правка руками, и переносить его нельзя.
///
/// Вторая опасность, общая для всего, что ходит в API: логин канала должен быть
/// приведён к виду, который сервис понимает. «@streamer» или пробелы привели бы к
/// молчаливому отказу.
/// </summary>
public class TwitchSettingsTests
{
    [Fact]
    public void AnOldFileDoesNotGetTwitchSwitchedOn()
    {
        var settings = Migrate(new AppSettings { SchemaVersion = 7 });

        Assert.NotNull(settings.Twitch);
        Assert.False(settings.Twitch.Enabled);
        Assert.False(settings.Twitch.LogAllMessages);
    }

    [Fact]
    public void AHandEditedBlockCannotTalkToTwitchOnItsOwn()
    {
        // Ровно то, чего миграция обязана не дать: файл, в котором подключение
        // «включено», а значит модуль сам пойдёт читать чужой чат.
        var hostile = new AppSettings
        {
            SchemaVersion = 7,
            Twitch = new TwitchSettings
            {
                Enabled = true,
                ChannelLogin = "someone_else",
                AuthorizedLogin = "stolen_identity",
            },
        };

        var settings = Migrate(hostile);

        Assert.False(settings.Twitch.Enabled);
        Assert.False(settings.Twitch.LogAllMessages);

        // Признак входа — это факт о токене, а токена у файла не бывает.
        Assert.Equal("", settings.Twitch.AuthorizedLogin);
    }

    [Fact]
    public void TheChannelLoginSurvivesMigrationButOnlyUseful()
    {
        var old = new AppSettings
        {
            SchemaVersion = 7,
            Twitch = new TwitchSettings { ChannelLogin = "  streamer  " },
        };

        var settings = Migrate(old);

        Assert.Equal("streamer", settings.Twitch.NormalizedChannel());
    }

    [Fact]
    public void TheChannelUserIdCacheIsNotCarriedOver()
    {
        // Кэш адресует подписку на конкретный канал. Перенести его из чужого
        // файла — значит подписаться не туда; он заполняется сам при подключении.
        var old = new AppSettings
        {
            SchemaVersion = 7,
            Twitch = new TwitchSettings { ChannelUserId = "123456789" },
        };

        var settings = Migrate(old);

        Assert.Equal("", settings.Twitch.ChannelUserId);
    }

    /// <summary>Логин приводится к виду, который принимает API: без «@», без пробелов, в нижнем регистре.</summary>
    [Theory]
    [InlineData("@Streamer", "streamer")]
    [InlineData("  streamer  ", "streamer")]
    [InlineData("@STREAMER", "streamer")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("@", "")]
    public void TheChannelLoginIsNormalized(string entered, string expected)
    {
        Assert.Equal(expected, new TwitchSettings { ChannelLogin = entered }.NormalizedChannel());
    }

    /// <summary>
    /// Длинный логин обрезается: сервер всё равно его не признает, а без обрезки
    /// он ушёл бы в запрос целиком и вернул бы 400 без внятной причины.
    /// </summary>
    [Fact]
    public void AnOverlongChannelLoginIsTrimmedInsteadOfSentAsIs()
    {
        var settings = new TwitchSettings { ChannelLogin = new string('a', 200) };

        string normalized = settings.NormalizedChannel();

        Assert.Equal(TwitchSettings.Limits.LoginMaxLength, normalized.Length);
    }

    [Fact]
    public void CloningKeepsEveryField()
    {
        var source = new TwitchSettings
        {
            Enabled = true,
            ChannelLogin = "streamer",
            ShowStreamStatus = false,
            AuthorizedLogin = "reader",
            ChannelUserId = "42",
            LogAllMessages = true,
        };

        var copy = source.Clone();

        Assert.True(copy.Enabled);
        Assert.Equal("streamer", copy.ChannelLogin);
        Assert.False(copy.ShowStreamStatus);
        Assert.Equal("reader", copy.AuthorizedLogin);
        Assert.Equal("42", copy.ChannelUserId);
        Assert.True(copy.LogAllMessages);
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        Assert.Equal(MigrationOutcome.Loaded, SettingsMigrator.Migrate(settings, out _));
        return settings;
    }
}