using SoundMeeter.Core.Entities;

namespace SoundMeeter.Core.Contract;

public interface IInstalledAppsService
{
    Task<List<InstalledApp>> GetInstalledAppsAsync();
    string? GetAppIconPath(string? installLocation, string? executablePath);
}
