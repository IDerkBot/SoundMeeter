using Microsoft.Win32;
using SoundMeeter.Models;
using System.IO;
using System.Text.Json;

namespace SoundMeeter.Services
{
    public class InstalledAppsService : IInstalledAppsService
    {
        private readonly SystemAppsFilterConfig _filterConfig;

        public InstalledAppsService()
        {
            _filterConfig = LoadFilterConfig();
        }

        private SystemAppsFilterConfig LoadFilterConfig()
        {
            try
            {
                // Путь к файлу в выходной папке (рядом с .exe)
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "system_apps_filter.json");

                if (!File.Exists(filePath))
                {
                    // Fallback: путь к исходному файлу в проекте (для отладки)
                    filePath = Path.Combine(Directory.GetCurrentDirectory(), "Resources", "system_apps_filter.json");
                }

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<SystemAppsFilterConfig>(json) ?? new SystemAppsFilterConfig();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не удалось загрузить фильтр: {ex.Message}");
            }

            // Если файл не найден — возвращаем пустой конфиг (ничего не фильтруется)
            return new SystemAppsFilterConfig();
        }

        public Task<List<InstalledApp>> GetInstalledAppsAsync()
        {
            return Task.Run(() =>
            {
                var apps = new List<InstalledApp>();
                var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var registryPaths = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var path in registryPaths)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(path);
                        if (key == null) continue;

                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            var displayName = subKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(displayName)) continue;

                            if (!seenApps.Add(displayName)) continue;
                            if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) continue;

                            if (IsSystemApp(displayName, subKey.GetValue("Publisher") as string))
                                continue;

                            var installLocation = subKey.GetValue("InstallLocation") as string;
                            var publisher = subKey.GetValue("Publisher") as string;
                            var version = subKey.GetValue("DisplayVersion") as string;
                            var displayIcon = subKey.GetValue("DisplayIcon") as string;
                            var uninstallString = subKey.GetValue("UninstallString") as string;

                            var executablePath = FindExecutablePath(installLocation, displayIcon, uninstallString);

                            apps.Add(new InstalledApp
                            {
                                Name = displayName,
                                InstallLocation = installLocation,
                                Publisher = publisher,
                                Version = version,
                                ExecutablePath = executablePath,
                                IconPath = GetIconPath(displayIcon, executablePath)
                            });
                        }
                    }
                    catch { }
                }

                return apps.OrderBy(a => a.Name).ToList();
            });
        }

        private bool IsSystemApp(string displayName, string? publisher)
        {
            var nameLower = displayName.ToLowerInvariant();
            var publisherLower = (publisher ?? "").ToLowerInvariant();

            foreach (var keyword in _filterConfig.SystemNameKeywords)
            {
                if (nameLower.Contains(keyword.ToLowerInvariant()))
                    return true;
            }

            foreach (var keyword in _filterConfig.SystemPublisherKeywords)
            {
                if (publisherLower.Contains(keyword.ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        private string? FindExecutablePath(string? installLocation, string? displayIcon, string? uninstallString)
        {
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
            {
                try
                {
                    var exeFiles = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                    var mainExe = exeFiles.FirstOrDefault(f =>
                        !f.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains("setup", StringComparison.OrdinalIgnoreCase));

                    if (mainExe != null)
                        return mainExe;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(displayIcon) && displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var iconPath = displayIcon.Split(',')[0];
                if (File.Exists(iconPath))
                    return iconPath;
            }

            return null;
        }

        private string? GetIconPath(string? displayIcon, string? executablePath)
        {
            if (!string.IsNullOrEmpty(displayIcon))
            {
                var iconPath = displayIcon.Split(',')[0];
                if (File.Exists(iconPath) && (iconPath.EndsWith(".ico") || iconPath.EndsWith(".exe")))
                    return iconPath;
            }

            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                return executablePath;
            }

            return null;
        }

        public string? GetAppIconPath(string? installLocation, string? executablePath)
        {
            return GetIconPath(null, executablePath);
        }
    }
}
