using System.Text.Json.Serialization;

namespace SoundMeeter.Update.Contract.Implementation;

/// <summary>
/// Ответ GET https://api.github.com/repos/{owner}/{repo}/releases/latest и
/// списка /releases. Внутренний DTO: наружу торчат только <see cref="UpdateInfo"/>,
/// <see cref="UpdateRelease"/> и <see cref="UpdateAsset"/>.
/// </summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    /// <summary>
    /// Черновик виден только автору через API, но в списке релизов он попасть
    /// может (например, при своём ключе), и показывать его в changelog нельзя.
    /// </summary>
    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto> Assets { get; set; } = new();
}
