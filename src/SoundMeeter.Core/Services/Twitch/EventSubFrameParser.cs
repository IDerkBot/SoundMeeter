using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Одно сообщение из EventSub WebSocket, разобранное до нужных полей.
///
/// Разбор отделён от транспорта намеренно: протокол меняется ( Twitch переводит
/// IRC в EventSub, и формат сообщений уже отличается), а вот что модулю нужно —
/// логин автора и текст реплики — останется прежним. Проверки разбора не должны
/// требовать сокета.
/// </summary>
public sealed record TwitchChatMessage(string User, string Text);

/// <summary>Разобранное сообщение протокола: что это за кадр.</summary>
public enum EventSubFrame
{
    /// <summary>Неизвестный кадр или мусор. Игнорируется.</summary>
    Unknown,

    /// <summary>Сессия открыта, сервер ждёт подписку. В кадре — идентификатор сессии.</summary>
    Welcome,

    /// <summary>Соединение живо. Ничего не делает.</summary>
    Keepalive,

    /// <summary>
    /// Сервер просит переподключиться на другой адрес и закрывает старое соединение.
    /// В кадре — новый адрес.
    /// </summary>
    Reconnect,

    /// <summary>Подписка отозвана: аккаунт удалён, доступ отозван или версия события больше не поддерживается.</summary>
    Revocation,

    /// <summary>Сообщение из чата.</summary>
    ChatMessage,
}

/// <summary>
/// Разбор кадров EventSub WebSocket (SM-F01).
///
/// Чистая функция от строки: ни сокета, ни потоков, ни времени. Поэтому её можно
/// проверить обычными проверками, а транспорт — отдельно и тоже без сети.
///
/// ЧТО ЗДЕСЬ ВАЖНО ДЛЯ ЗВУЧАЩЕГО МОДУЛЯ.
///
/// Первое: Twitch шлёт событие на КАЖДОЕ сообщение чата, а не только на команды.
/// Модулю нужны только реплики, поэтому разбор обязан отличать сообщение от
/// системных уведомлений (подписки, рейды, точки) — иначе модуль озвучивал бы
/// «raider» при входе зрителя.
///
/// Второе: у сообщений чата есть фрагменты (message_fragments). Эмоции и значки
/// приходят отдельными элементами, и их текст не является тем, что написал человек.
/// Читать надо только части без <c>cheermote</c> и <c>emote</c> — иначе синтезатор
/// произносил бы «Kappa», которых в реплике не было.
///
/// Третье: Twitch переподключает сессию, присылая кадр reconnect. Его надо узнать
/// и взять из него адрес, иначе соединение молча перестанет приносить сообщения
/// через полминуты на эфире.
/// </summary>
public static class EventSubFrameParser
{
    /// <summary>
    /// Имя события с сообщениями чата. Именно на него подписывается модуль, и всё
    /// остальное (подписки, рейды, донаты) отсеивается по нему же.
    /// </summary>
    public const string ChatSubscriptionType = "channel.chat.message";

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Разбирает текстовый кадр.
    ///
    /// Возвращает null для всего, что не является сообщением чата, — это обычное
    /// дело: на каждый кадр с сообщением приходит множество служебных.
    /// </summary>
    public static (EventSubFrame Kind, TwitchChatMessage? Message, string? Value) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (EventSubFrame.Unknown, null, null);

        try
        {
            using var document = JsonDocument.Parse(json, Options);
            var root = document.RootElement;

            // Кадр — это объект. Если пришло что-то другое (массив, число, строка),
            // обращаться к полям нельзя: TryGetProperty на не-объекте бросает, а не
            // возвращает false.
            if (root.ValueKind != JsonValueKind.Object) return (EventSubFrame.Unknown, null, null);

            if (!root.TryGetProperty("metadata", out var metadata)) return (EventSubFrame.Unknown, null, null);

            string kind = metadata.TryGetProperty("message_type", out var type) ? type.GetString() ?? "" : "";
            if (!root.TryGetProperty("payload", out var payload)) return (EventSubFrame.Unknown, null, null);

            return kind switch
            {
                "session_welcome" => (EventSubFrame.Welcome, null, SessionId(payload)),
                "session_keepalive" => (EventSubFrame.Keepalive, null, null),

                // Адрес переподключения берём как есть: Twitch требует использовать
                // его дословно, и любая правка (например, добавление параметра) делает
                // адрес недействительным.
                "session_reconnect" => (EventSubFrame.Reconnect, null, ReconnectUrl(payload)),

                "revocation" => (EventSubFrame.Revocation, null, SubscriptionStatus(payload)),
                "notification" => ParseNotification(payload),
                _ => (EventSubFrame.Unknown, null, null),
            };
        }
        catch (JsonException)
        {
            // Мусор в кадре — не повод ронять соединение: сервер шлёт формат, который
            // мы не знаем, и это не наша поломка.
            return (EventSubFrame.Unknown, null, null);
        }
    }

    private static (EventSubFrame, TwitchChatMessage?, string?) ParseNotification(JsonElement payload)
    {
        // Событие не channel.chat.message — значит это подписка, рейд, точка или
        // что-то ещё. Модулю озвучивать нечего.
        // Подписка сообщает, что за событие пришло. Если объекта нет — разбирать нечего,
        // EventSub его всегда присылает вместе с событием.
        if (!payload.TryGetProperty("subscription", out var subscription))
            return (EventSubFrame.Unknown, null, null);

        string subscribed = subscription.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

        // Подписки, рейды, точки — всё это тоже приходит в кадрах уведомлений, но
        // модулю озвучки не нужно. Без проверки он объявил бы весь чат на эфире.
        if (!string.Equals(subscribed, ChatSubscriptionType, StringComparison.Ordinal))
            return (EventSubFrame.Unknown, null, null);

        if (!payload.TryGetProperty("event", out var data)) return (EventSubFrame.Unknown, null, null);

        string login = data.TryGetProperty("chatter_user_login", out var author)
            ? author.GetString() ?? ""
            : data.TryGetProperty("user_login", out var fallback)
                ? fallback.GetString() ?? ""
                : "";

        string text = ReadPlainText(data);
        if (login.Length == 0 || text.Length == 0) return (EventSubFrame.Unknown, null, null);

        return (EventSubFrame.ChatMessage, new TwitchChatMessage(login, text), null);
    }

    /// <summary>
    /// Собирает текст реплики из фрагментов, пропуская эмоции и значки.
    ///
    /// У Twitch сообщение разбито на фрагменты, и у каждого свой тип: <c>text</c> —
    /// то, что написал человек, а <c>emote</c> и <c>cheermote</c> — картинки.
    /// Собирать всё подряд нельзя: тогда синтезатор произносил бы «Kappa» и «Cheer100»
    /// вместо слов, которых в реплике не было.
    ///
    /// Если фрагментов нет (формат изменился или версия события незнакомая),
    /// берётся целиком <c>message.text</c> — лучше лишнее слово, чем тишина.
    /// </summary>
    private static string ReadPlainText(JsonElement data)
    {
        if (data.TryGetProperty("message", out var message)
            && message.TryGetProperty("fragments", out var fragments)
            && fragments.ValueKind == JsonValueKind.Array
            && fragments.GetArrayLength() > 0)
        {
            var parts = new List<string>(fragments.GetArrayLength());

            foreach (var fragment in fragments.EnumerateArray())
            {
                string type = fragment.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

                // Произносим только напечатанный текст: картинки и значки вслух не
                // читаются, а их имена — технические идентификаторы.
                if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)) continue;

                if (fragment.TryGetProperty("text", out var value))
                {
                    string part = value.GetString() ?? "";
                    if (part.Length > 0) parts.Add(part);
                }
            }

            if (parts.Count > 0) return string.Concat(parts);
        }

        return data.TryGetProperty("message", out var plain)
               && plain.TryGetProperty("text", out var whole)
                ? whole.GetString() ?? ""
                : "";
    }

    private static string? SessionId(JsonElement payload)
    {
        if (!payload.TryGetProperty("session", out var session)) return null;

        return session.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static string? ReconnectUrl(JsonElement payload)
    {
        if (!payload.TryGetProperty("session", out var session)) return null;

        return session.TryGetProperty("reconnect_url", out var url) ? url.GetString() : null;
    }

    /// <summary>Причина отзыва подписки. Показывается пользователю как «подписка отозвана».</summary>
    private static string? SubscriptionStatus(JsonElement payload)
    {
        if (!payload.TryGetProperty("subscription", out var subscription)) return null;

        return subscription.TryGetProperty("status", out var status) ? status.GetString() : null;
    }
}