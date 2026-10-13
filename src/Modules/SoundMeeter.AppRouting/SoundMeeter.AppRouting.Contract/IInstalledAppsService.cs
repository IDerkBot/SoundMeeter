using SoundMeeter.AppRouting.Entities;

namespace SoundMeeter.AppRouting.Contract;

public interface IInstalledAppsService
{
    Task<List<InstalledApp>> GetInstalledAppsAsync();
    string? GetAppIconPath(string? installLocation, string? executablePath);
}
