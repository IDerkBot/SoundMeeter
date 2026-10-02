using SoundMeeter.Models;

namespace SoundMeeter.Services
{
    public interface IInstalledAppsService
    {
        Task<List<InstalledApp>> GetInstalledAppsAsync();
        string? GetAppIconPath(string? installLocation, string? executablePath);
    }
}
