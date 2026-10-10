using System.Text.Json.Serialization;

namespace SoundMeeter.Twitch.Entities;

/// <summary>
/// Токен доступа Twitch: пара «токен + обновляющий токен» и срок.
///
/// Отдельный класс, а не три строки, потому что пара всегда ходит вместе:
/// обновляющий токен выдаётся вместе с доступным и без доступа не имеет смысла,
/// а разъезжающаяся пара означала бы, что при следующем обновлении токен молча
/// потеряется.
/// </summary>
public sealed class TwitchToken
{
    /// <summary>Токен для обращения к API. Короткоживущий: около четырёх часов.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// Токен для продления. Одноразовый: Twitch выдаёт новый при каждом обновлении,
    /// а использованный становится недействительным — это защита от кражи перехватом.
    /// Поэтому он меняется при каждом обновлении, а не переиспользуется.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    /// <summary>Момент, после которого доступный токен нельзя использовать (UTC).</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Логин аккаунта, под которым выдан токен. Для показа в интерфейсе.</summary>
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    /// <summary>Идентификатор пользователя по токену. Адресует подписку на чат.</summary>
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>
    /// Токен пригоден к использованию.
    ///
    /// Проверка с запасом <see cref="ExpiryLeeway"/>: обновление требует запроса в
    /// сеть, а если он не пройдёт, соединённое приложение останется без чата до
    /// следующей попытки. Полчаса запаса означают, что подключение переживёт
    /// кратковременную потерю сети и не рассыплется на пустом токене.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken)
        && ExpiresAt > DateTimeOffset.UtcNow + ExpiryLeeway;

    /// <summary>
    /// Запас до срока, после которого токен считается протухшим.
    ///
    /// Обновление требует запроса в сеть, а если он не пройдёт, соединённое
    /// приложение останется без чата до следующей попытки. Полчаса запаса
    /// означают, что подключение переживёт кратковременную потерю сети и не
    /// рассыплется на пустом токене.
    /// </summary>
    public static readonly TimeSpan ExpiryLeeway = TimeSpan.FromMinutes(30);
}
