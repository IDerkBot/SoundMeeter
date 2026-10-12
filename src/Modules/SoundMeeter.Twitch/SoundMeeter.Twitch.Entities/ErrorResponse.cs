using System.Text.Json.Serialization;

namespace SoundMeeter.Twitch.Entities;

/// <summary>Ответ с ошибкой от Twitch.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
