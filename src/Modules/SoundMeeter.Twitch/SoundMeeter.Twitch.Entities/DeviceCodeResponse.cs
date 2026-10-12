using System.Text.Json.Serialization;

namespace SoundMeeter.Twitch.Entities;

/// <summary>Ответ на запрос кода входа.</summary>
public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
}
