using System.Text.Json.Serialization;

namespace SoundMeeter.Twitch.Entities;

/// <summary>Ответ на обмен кода или обновляющего токена.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string[]? Scope { get; set; }

    /// <summary>
    /// Логин и идентификатор аккаунта. Twitch присылает их только в отдельном
    /// запросе по готовому токену, поэтому при обновлении их переносят из прошлой
    /// пары — см. обновление токена в <see cref="TwitchAuthService"/>.
    /// </summary>
    public string? Login { get; set; }

    /// <summary>Идентификатор аккаунта. Адресует подписку на чат.</summary>
    public string? UserId { get; set; }
}
