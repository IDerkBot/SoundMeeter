using SoundMeeter.Services.Twitch;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Разбор кадров EventSub WebSocket (SM-F01).
///
/// Это самое важное место для модуля озвучки, и проверяется оно без сокета,
/// потому что от него зависит, ЧТО вообще будет произнесено.
///
/// Главное, что здесь ломается легко и тихо: Twitch шлёт событие на каждое
/// сообщение чата, а не только на команды. Если разбор не отличает сообщение от
/// служебного уведомления, модуль озвучит «raider» при входе зрителя, а если не
/// отбрасывает эмоции — произнесёт «Kappa» вместо написанного человеком текста.
/// Оба дефекта не падают, а просто говорят не то, и на эфире это хуже любой ошибки.
///
/// Второе: сообщения приходят фрагментами, и у фрагментов есть типы. Значки и
/// эмоции — технические идентификаторы, вслух они не произносятся.
/// </summary>
public class EventSubFrameParserTests
{
    [Fact]
    public void AWelcomeCarriesTheSessionIdentifier()
    {
        var (kind, _, value) = EventSubFrameParser.Parse(Welcome());

        // Идентификатор сессии обязателен: без него подписку создать нельзя, и
        // Twitch закрывает соединение через десять секунд.
        Assert.Equal(EventSubFrame.Welcome, kind);
        Assert.Equal("AQoQILE98gtqShGmLD7AM6yJThAB", value);
    }

    [Fact]
    public void AKeepaliveIsRecognisedAndCarriesNothing()
    {
        var (kind, message, value) = EventSubFrameParser.Parse(
            """{"metadata":{"message_type":"session_keepalive"},"payload":{}}""");

        Assert.Equal(EventSubFrame.Keepalive, kind);
        Assert.Null(message);
        Assert.Null(value);
    }

    [Fact]
    public void AReconnectRequestCarriesTheUrlToUseVerbatim()
    {
        var (kind, _, value) = EventSubFrameParser.Parse(
            "{\"metadata\":{\"message_type\":\"session_reconnect\"}," +
            "\"payload\":{\"session\":{\"id\":\"X\"," +
            "\"reconnect_url\":\"wss://eventsub.wss.twitch.tv/ws?new\"}}}");

        // Адрес используется дословно: Twitch отклоняет изменённый, а дописывание
        // параметра «для аккуратности» обрывает подписку на эфире.
        Assert.Equal(EventSubFrame.Reconnect, kind);
        Assert.Equal("wss://eventsub.wss.twitch.tv/ws?new", value);
    }

    [Fact]
    public void AChatMessageGivesTheAuthorAndTheText()
    {
        var (kind, message, _) = EventSubFrameParser.Parse(Chat("!tts привет стрим"));

        Assert.Equal(EventSubFrame.ChatMessage, kind);
        Assert.NotNull(message);
        Assert.Equal("viewer", message!.User);
        Assert.Equal("!tts привет стрим", message.Text);
    }

    [Fact]
    public void EmotesAreNotSpokenBecauseTheyArePictures()
    {
        var (kind, message, _) = EventSubFrameParser.Parse(ChatWithFragments(
            [("text", "привет "), ("emote", "Kappa"), ("text", " мир")]));

        Assert.Equal(EventSubFrame.ChatMessage, kind);

        // Произнести «Kappa» — значит озвучить то, чего в реплике не было.
        Assert.Equal("привет  мир", message!.Text);
        Assert.DoesNotContain("Kappa", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CheermotesAreNotSpokenEither()
    {
        // «Cheer100» — технический идентификатор значка, а не написанное слово.
        var (_, message, _) = EventSubFrameParser.Parse(ChatWithFragments(
            [("text", "спасибо "), ("cheermote", "Cheer100")]));

        Assert.DoesNotContain("Cheer100", message!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextAroundAnEmoteKeepsBothSides()
    {
        // Проверка, что при выбрасывании эмоции не выбрасывается и остальное:
        // типичная ошибка — вернуть текст до первой эмоции и потерять вторую часть.
        var (_, message, _) = EventSubFrameParser.Parse(ChatWithFragments(
            [("emote", "Kappa"), ("text", "здарова"), ("emote", "LUL")]));

        Assert.Equal("здарова", message!.Text);
    }

    [Fact]
    public void AMessageWithoutFragmentsFallsBackToTheWholeText()
    {
        // Формат может измениться, и тогда фрагментов не будет. Молчать тогда нельзя:
        // лучше произнести лишнее слово, чем не озвучить «!tts» вообще.
        var json = Notification("channel.chat.message",
            "{\"chatter_user_login\":\"viewer\",\"message\":{\"text\":\"!tts тест\"}}");

        var (kind, message, _) = EventSubFrameParser.Parse(json);

        Assert.Equal(EventSubFrame.ChatMessage, kind);
        Assert.Equal("!tts тест", message!.Text);
    }

    [Fact]
    public void NotificationsThatAreNotChatMessagesAreNotSpoken()
    {
        // Подписки, рейды, точки, донаты — это события EventSub, но не сообщения
        // чата. Модулю озвучки они не нужны, и без этой проверки он объявил бы весь
        // чат на эфире.
        foreach (string subscriptionType in new[]
                 {
                     "channel.follow", "channel.subscribe", "channel.raid",
                     "channel.cheer", "channel.raid.user", "stream.online",
                     "channel.ban", "channel.unban", "channel.poll.end",
                 })
        {
            string json = Notification(subscriptionType,
                """{"user_login":"viewer","text":"!tts привет"}""");

            var (kind, message, _) = EventSubFrameParser.Parse(json);

            Assert.Equal(EventSubFrame.Unknown, kind);
            Assert.Null(message);
        }
    }

    [Fact]
    public void AMissingAuthorIsNotTreatedAsAMessage()
    {
        // Реплика без автора не произносится: неизвестно, кому и по какому голосу.
        var json = Notification("channel.chat.message", "{\"message\":{\"text\":\"!tts привет\"}}");

        Assert.Equal(EventSubFrame.Unknown, EventSubFrameParser.Parse(json).Kind);
    }

    [Fact]
    public void AnEmptyMessageIsNotTreatedAsAMessage()
    {
        // Пустая реплика — обычное дело (отправка картинки). Модуль не должен на неё
        // реагировать.
        var json = Notification("channel.chat.message",
            "{\"chatter_user_login\":\"viewer\",\"message\":{\"text\":\"\"}}");

        Assert.Equal(EventSubFrame.Unknown, EventSubFrameParser.Parse(json).Kind);
    }

    [Fact]
    public void AMessageOfOnlyEmotesHasNothingToSay()
    {
        // «Kappa» и больше ничего: произносить нечего. Модуль просто не получит
        // реплику, и это правильно — озвучивать «Kappa» значило бы сказать то, чего
        // никто не писал.
        var (kind, message, _) = EventSubFrameParser.Parse(ChatWithFragments([("emote", "Kappa")]));

        Assert.Equal(EventSubFrame.Unknown, kind);
        Assert.Null(message);
    }

    [Fact]
    public void ARevokedSubscriptionCarriesItsReason()
    {
        // Причина отзыва показывается пользователю: «доступ отозван» и «аккаунт
        // удалён» требуют разных действий.
        string json = """
            {"metadata":{"message_type":"revocation"},
             "payload":{"subscription":{"type":"channel.chat.message","status":"authorization_revoked"}}}
            """;

        var (kind, _, value) = EventSubFrameParser.Parse(json);

        Assert.Equal(EventSubFrame.Revocation, kind);
        Assert.Equal("authorization_revoked", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не json вовсе")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void GarbageIsIgnoredInsteadOfThrowing(string json)
    {
        // Сервер присылает формат, который мы можем не знать. Рвать соединение на
        // этом нельзя: связь дороже, чем непонимание одного кадра.
        var (kind, message, value) = EventSubFrameParser.Parse(json);

        Assert.Equal(EventSubFrame.Unknown, kind);
        Assert.Null(message);
        Assert.Null(value);
    }

    [Fact]
    public void AnUnknownMessageTypeIsIgnored()
    {
        var (kind, _, _) = EventSubFrameParser.Parse(
            """{"metadata":{"message_type":"session_something_new"},"payload":{}}""");

        Assert.Equal(EventSubFrame.Unknown, kind);
    }

    [Fact]
    public void LargeMessagesSurviveTheParse()
    {
        // Длинная реплика с несколькими словами — обычное дело, и обрезка её молча
        // привела бы к тому, что синтезатор произносит обрывок.
        string longText = string.Join(' ', Enumerable.Repeat("слово", 200));

        var (_, message, _) = EventSubFrameParser.Parse(Chat(longText));

        Assert.Equal(longText, message!.Text);
    }

    private static string Welcome() => """
        {"metadata":{"message_type":"session_welcome"},
         "payload":{"session":{"id":"AQoQILE98gtqShGmLD7AM6yJThAB","status":"connected",
         "keepalive_timeout_seconds":10,"reconnect_url":null}}}
        """;

    /// <summary>
    /// Собирает кадр-уведомление. Строка собирается конкатенацией, а не вставкой в
    /// шаблон: в JSON много фигурных скобок подряд, и вставка в такой шаблон
    /// превращается в нечитаемую мешанину из экранированных скобок.
    /// </summary>
    private static string Notification(string subscriptionType, string eventJson) =>
        "{\"metadata\":{\"message_type\":\"notification\",\"subscription_type\":\"" + subscriptionType + "\"}," +
        "\"payload\":{\"subscription\":{\"type\":\"" + subscriptionType + "\"},\"event\":" + eventJson + "}}";

    private static string Chat(string text) =>
        Notification("channel.chat.message",
            "{\"chatter_user_login\":\"viewer\",\"message\":{\"text\":" + Json(text) + "}}");

    private static string ChatWithFragments((string Type, string Text)[] fragments)
    {
        var parts = new List<string>(fragments.Length);
        foreach (var (type, text) in fragments)
            parts.Add("{\"type\":\"" + type + "\",\"text\":" + Json(text) + "}");

        return Notification("channel.chat.message",
            "{\"chatter_user_login\":\"viewer\",\"message\":{\"fragments\":[" +
            string.Join(",", parts) + "]}}");
    }

    /// <summary>Экранирует значение для вставки в JSON вручную — в проверках нет смысла тащить сериализатор.</summary>
    private static string Json(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}