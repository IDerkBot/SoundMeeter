using System.Text.Json.Serialization;

namespace SoundMeeter.Update.Entities;

/// <summary>Файл, приложенный к релизу.</summary>
public sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
