using SoundMeeter.Models;
using SoundMeeter.Services.TextToSpeech;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Разбор реплик чата в команды модуля синтеза речи (SM-E01).
///
/// Это ровно та часть, которая сегодня проверяется вручную из окна настроек, а
/// завтра будет работать на живом чате Twitch. Поэтому здесь она проверяется
/// полностью: подключение источника ничего в ней не меняет, и регрессия
/// (например, «!ttsvoice» начал читаться как фраза «voice …») иначе была бы
/// видна только в эфире.
/// </summary>
public class TtsChatCommandParserTests
{
    private static readonly string[] InstalledVoices = ["Microsoft Irina", "Microsoft Pavel", "Microsoft Zira"];

    private static TextToSpeechSettings Settings() => new()
    {
        Enabled = true,
        SpeakCommand = TextToSpeechSettings.DefaultSpeakCommand,
        VoiceCommand = TextToSpeechSettings.DefaultVoiceCommand,
        MaxMessageChars = 300,
        MaxMessagesPerMinute = 5,
        AllowVoiceChange = true,
    };

    private static TtsChatCommand Parse(string? user, string? text, TextToSpeechSettings? settings = null) =>
        TtsChatCommandParser.Parse(user, text, settings ?? Settings(), IsKnown);

    private static bool IsKnown(string voice) =>
        InstalledVoices.Contains(voice, StringComparer.OrdinalIgnoreCase);

    #region Озвучка

    [Theory]
    [InlineData("!tts привет стример", "привет стример")]
    [InlineData("!tts   лишние   пробелы  ", "лишние пробелы")]
    [InlineData("  !tts с ведущими пробелами", "с ведущими пробелами")]
    [InlineData("!TTS верхний регистр команды", "верхний регистр команды")]
    public void ASpeechCommandBecomesTheTextToRead(string message, string expected)
    {
        var command = Parse("viewer", message);

        Assert.True(command.IsAccepted);
        Assert.Equal(TtsChatAction.Speak, command.Action);
        Assert.Equal(expected, command.Text);
        Assert.Equal("viewer", command.User);
    }

    /// <summary>
    /// Правило отделителя строже, чем регистр: «!TTS» — команда, а «!ttsTtS» уже
    /// нет, потому что после команды стоит не пробел. Иначе под разбор попало бы
    /// что угодно, начинающееся с «!tts».
    /// </summary>
    [Fact]
    public void MixedCaseIsAcceptedOnlyWhenTheCommandEndsTheWord()
    {
        Assert.Equal(TtsChatAction.Speak, Parse("viewer", "!TTS привет").Action);
        Assert.Equal(TtsChatReject.NotACommand, Parse("viewer", "!ttsTtS привет").Reason);
    }

    /// <summary>
    /// Регистр команды не важен — «!TTS» пишут постоянно, — но регистр текста важен:
    /// он не табулируется, иначе имя или модель в реплике читались бы капсом.
    /// </summary>
    [Fact]
    public void TheTextKeepsItsOwnCapitalization()
    {
        Assert.Equal("Привет, СтREAM", Parse("viewer", "!tts Привет, СтREAM").Text);
    }

    /// <summary>
    /// Команда должна быть в начале. Иначе «а ещё !tts привет» прочиталось бы
    /// вслух целиком, вместе со служебным словом, которое писали вовсе не для этого.
    /// </summary>
    [Theory]
    [InlineData("а ещё !tts привет")]
    [InlineData("кто-нибудь !tts привет")]
    public void ACommandInTheMiddleOfALineIsNotACommand(string message)
    {
        var command = Parse("viewer", message);

        Assert.Equal(TtsChatReject.NotACommand, command.Reason);
        Assert.False(command.IsAccepted);
    }

    /// <summary>
    /// «!ttsfoo» — не «!tts». Без проверки на разделитель соседние команды
    /// («!ttsvoice») распознавались бы как озвучка слова «voice».
    /// </summary>
    [Fact]
    public void ALongerWordStartingWithTheCommandIsNotACommand()
    {
        Assert.Equal(TtsChatReject.NotACommand, Parse("viewer", "!ttspeech сегодня").Reason);
        Assert.Equal(TtsChatAction.SetVoice, Parse("viewer", "!ttsvoice Microsoft Irina").Action);
    }

    [Theory]
    [InlineData("привет")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnOrdinaryLineIsSkippedSilently(string? message)
    {
        var command = Parse("viewer", message);

        Assert.Equal(TtsChatReject.NotACommand, command.Reason);
        Assert.Equal(TtsChatAction.None, command.Action);
    }

    [Fact]
    public void ASpeechCommandWithNothingToSayIsRejected()
    {
        Assert.Equal(TtsChatReject.EmptyText, Parse("viewer", "!tts").Reason);
        Assert.Equal(TtsChatReject.EmptyText, Parse("viewer", "!tts    ").Reason);
    }

    /// <summary>
    /// Реплики без команды не трогаем никак — иначе модуль читал бы весь чат.
    /// Это и есть первое требование к модулю.
    /// </summary>
    [Fact]
    public void OrdinaryChatIsNeverReadAloud()
    {
        Assert.False(Parse("viewer", "привет, как дела?").IsAccepted);
        Assert.False(Parse("viewer", "!othercommand привет").IsAccepted);
        Assert.False(Parse("viewer", "tts привет").IsAccepted);
    }

    #endregion

    #region Обрезка и чистка

    [Fact]
    public void ALongLineIsTrimmedButStillRead()
    {
        var settings = Settings();
        settings.MaxMessageChars = 20;

        var command = Parse("viewer", "!tts абракадабрабракадабрабракадабра", settings);

        Assert.True(command.IsAccepted);
        Assert.Equal(20, command.Text.Length);
        Assert.True(command.Truncated);
    }

    [Fact]
    public void AnOverlongLineIsNotMarkedTruncatedWhenItFits()
    {
        Assert.False(Parse("viewer", "!tts коротко").Truncated);
    }

    /// <summary>
    /// Ссылки не произносятся: TTS-боты и плагины добавляют их в каждое сообщение,
    /// и вслух они читались бы как «хекс эс эс эс колон слэш…».
    /// </summary>
    [Theory]
    [InlineData("!tts заходи https://twitch.tv/streamer", "заходи")]
    [InlineData("!tts заходи http://example.com.", "заходи")]
    [InlineData("!tts заходи www.example.com", "заходи")]
    [InlineData("!tts заходи ftp://files.example.com/x", "заходи")]
    public void LinksAreDropped(string message, string expected)
    {
        Assert.Equal(expected, Parse("viewer", message).Text);
    }

    [Fact]
    public void ALineConsistingOnlyOfLinksHasNothingToRead()
    {
        Assert.Equal(TtsChatReject.EmptyText, Parse("viewer", "!tts https://example.com").Reason);
    }

    #endregion

    #region Голоса

    [Fact]
    public void AVoiceCommandRemembersTheVoiceForThatUser()
    {
        var command = Parse("moderator", "!ttsvoice Microsoft Irina");

        Assert.True(command.IsAccepted);
        Assert.Equal(TtsChatAction.SetVoice, command.Action);
        Assert.Equal("Microsoft Irina", command.Voice);
        Assert.Equal("moderator", command.User);
    }

    /// <summary>
    /// Команда выбора голоса проверяется ПЕРЕД командой озвучки. Иначе «!ttsvoice»
    /// прочиталось бы как фраза «voice Microsoft Irina» — то есть модератор вместо
    /// выбора голоса получил бы лишнюю озвучку в эфир.
    /// </summary>
    [Fact]
    public void TheVoiceCommandIsNeverMistakenForSpeech()
    {
        var command = Parse("moderator", "!ttsvoice Microsoft Irina");

        Assert.NotEqual(TtsChatAction.Speak, command.Action);
        Assert.Equal(string.Empty, command.Text);
    }

    /// <summary>
    /// Несуществующий голос отвергается: принять его означало бы записать в
    /// настройки имя, которого нет, и пользователь обнаружил бы это через эфир.
    /// </summary>
    [Fact]
    public void AnUnknownVoiceIsRejected()
    {
        var command = Parse("moderator", "!ttsvoice Microsoft Hal 9000");

        Assert.Equal(TtsChatReject.UnknownVoice, command.Reason);
        Assert.False(command.IsAccepted);
    }

    [Fact]
    public void VoiceNamesAreMatchedWithoutCaseSensitivity()
    {
        Assert.Equal(TtsChatAction.SetVoice, Parse("moderator", "!ttsvoice microsoft irina").Action);
    }

    [Fact]
    public void VoiceChangeCanBeTurnedOffInTheSettings()
    {
        var settings = Settings();
        settings.AllowVoiceChange = false;

        Assert.Equal(TtsChatReject.VoiceChangeDisabled, Parse("moderator", "!ttsvoice Microsoft Irina", settings).Reason);
    }

    [Fact]
    public void AVoiceCommandWithNoVoiceIsRejected()
    {
        Assert.Equal(TtsChatReject.EmptyText, Parse("moderator", "!ttsvoice").Reason);
        Assert.Equal(TtsChatReject.EmptyText, Parse("moderator", "!ttsvoice    ").Reason);
    }

    #endregion

    #region Игнор и логин

    [Fact]
    public void IgnoredUsersAreSilentEvenWithACommand()
    {
        var settings = Settings();
        settings.IgnoredUsers.Add("bot");

        Assert.Equal(TtsChatReject.IgnoredUser, Parse("BOT", "!tts продам рекламу", settings).Reason);
        Assert.Equal(TtsChatReject.IgnoredUser, Parse("bot", "!ttsvoice Microsoft Irina", settings).Reason);
    }

    /// <summary>
    /// «@» — это обращение в чате, а не часть логина. Регистр при этом
    /// сохраняется: логины Twitch пишут по канону, и в списке голосов полезно
    /// видеть имя в том виде, в каком его знает Twitch.
    /// </summary>
    [Fact]
    public void TheAtSignIsNotPartOfTheLogin()
    {
        Assert.Equal("moderator", Parse("@moderator", "!tts привет").User);
        Assert.Equal("Moderator", TtsChatCommandParser.NormalizeUser("  @Moderator  "));
    }

    [Fact]
    public void AnAbsurdlyLongLoginIsTrimmed()
    {
        var login = new string('x', TextToSpeechSettings.Limits.UserNameMaxLength + 50);

        Assert.Equal(TextToSpeechSettings.Limits.UserNameMaxLength, Parse(login, "!tts привет").User.Length);
    }

    #endregion

    #region Свои команды

    [Fact]
    public void BothCommandsCanBeRenamed()
    {
        var settings = Settings();
        settings.SpeakCommand = "!озвучь";
        settings.VoiceCommand = "!голос";

        Assert.Equal(TtsChatAction.Speak, Parse("viewer", "!озвучь привет", settings).Action);
        Assert.Equal(TtsChatAction.SetVoice, Parse("viewer", "!голос Microsoft Irina", settings).Action);
        Assert.Equal(TtsChatReject.NotACommand, Parse("viewer", "!tts привет", settings).Reason);
    }

    [Fact]
    public void AMissingBangIsAddedToTheCommand()
    {
        var settings = Settings();
        settings.SpeakCommand = "озвучь";

        Assert.Equal(TtsChatAction.Speak, Parse("viewer", "!озвучь привет", settings).Action);
    }

    #endregion
}
