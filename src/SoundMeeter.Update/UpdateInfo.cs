using System.Globalization;

namespace SoundMeeter.Models;

/// <summary>Файл релиза, пригодный для автообновления (portable zip).</summary>
public sealed class UpdateAsset
{
    public string Name { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }

    public string HumanSize
    {
        get
        {
            double size = Size;
            string[] units = ["B", "KB", "MB", "GB"];
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{Size} {units[unit]}")
                : string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
        }
    }
}

/// <summary>Найденный релиз, который новее установленной версии.</summary>
public sealed class UpdateInfo
{
    public required AppVersion Version { get; init; }
    public string TagName { get; init; } = "";
    public string Title { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Portable-архив для автоустановки. null — у релиза нет подходящего zip.</summary>
    public UpdateAsset? Asset { get; init; }

    /// <summary>Автоустановка возможна только при наличии zip-ассета.</summary>
    public bool CanInstall => Asset is not null;
}

/// <summary>Итог проверки обновлений: без исключений, чтобы UI не падал из-за сети.</summary>
public sealed record UpdateCheckResult(bool Success, bool UpdateAvailable, UpdateInfo? Update, string Message)
{
    public static UpdateCheckResult Failed(string message) => new(false, false, null, message);
}
