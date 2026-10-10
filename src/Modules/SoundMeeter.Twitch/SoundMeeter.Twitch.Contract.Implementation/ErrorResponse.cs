using System.Text.Json.Serialization;

namespace SoundMeeter.Twitch.Contract.Implementation;

/// <summary>Ответ с ошибкой от Twitch.</summary>
internal sealed class ErrorResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
