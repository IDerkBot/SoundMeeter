namespace SoundMeeter.Update.Entities;

/// <summary>Итог проверки обновлений: без исключений, чтобы UI не падал из-за сети.</summary>
public sealed record UpdateCheckResult(bool Success, bool UpdateAvailable, UpdateInfo? Update, string Message)
{
    public static UpdateCheckResult Failed(string message) => new(false, false, null, message);
}
