using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SoundMeeter.Services
{
    public class InstalledAppsService : IInstalledAppsService
    {
        /// <summary>
        /// Имя файла с ключевыми словами системных приложений и встроенной копии
        /// этого же файла. Файл лежит рядом с exe и правится пользователем; встроенная
        /// копия нужна, чтобы создать его при первом запуске публикации одним файлом,
        /// где рядом с exe ничего нет.
        /// </summary>
        internal const string FilterFileName = "system_apps_filter.json";

        internal const string FilterResource = "soundmeeter.system-apps-filter.json";

        private readonly SystemAppsFilterConfig _filterConfig;
        private readonly ILogger _logger = AppLog.For<InstalledAppsService>();

        public InstalledAppsService()
        {
            _filterConfig = LoadFilterConfig();
        }

        /// <summary>Путь к файлу фильтра рядом с exe — его можно править вручную.</summary>
        internal static string FilterPath => Path.Combine(
            AppContext.BaseDirectory, "Resources", FilterFileName);

        /// <summary>
        /// Фильтр читается из файла рядом с exe, а если его нет (первый запуск сборки
        /// одним файлом) — создаётся из встроенной копии и дальше живёт на диске.
        ///
        /// Так пользователь может и дополнять список своих системных приложений, и
        /// правки переживают обновление: обновление удаляет рядом с exe только
        /// остатки сборки (dll/pdb), а json и папки не трогает.
        ///
        /// Приоритет: файл пользователя → встроенная копия → пустой фильтр (то есть
        /// ничего не фильтруется, но приложение работает).
        /// </summary>
        private SystemAppsFilterConfig LoadFilterConfig()
        {
            string path = FilterPath;

            try
            {
                if (File.Exists(path))
                {
                    var config = JsonSerializer.Deserialize<SystemAppsFilterConfig>(
                        WithoutBom(File.ReadAllBytes(path)));
                    if (config != null)
                    {
                        _logger.LogInformation("Загружен фильтр системных приложений: {File}", path);
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось прочитать фильтр системных приложений: {File}", path);
            }

            return LoadEmbedded(path);
        }

        /// <summary>
        /// Встроенная копия фильтра. Заодно кладёт её на диск, если файла нет:
        /// пользовательский список должен быть редактируемым, а в single-file
        /// публикации рядом с exe сам он не появится.
        /// </summary>
        private SystemAppsFilterConfig LoadEmbedded(string path)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FilterResource);
                if (stream is null)
                {
                    _logger.LogError("Встроенный ресурс {Resource} не найден в сборке", FilterResource);
                    return new SystemAppsFilterConfig();
                }

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                byte[] bytes = memory.ToArray();

                SeedFile(path, bytes);

                var config = JsonSerializer.Deserialize<SystemAppsFilterConfig>(WithoutBom(bytes));
                _logger.LogInformation("Загружен фильтр системных приложений из встроенной копии: {File}", path);
                return config ?? new SystemAppsFilterConfig();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось загрузить фильтр системных приложений: {Message}", ex.Message);
            }

            // Если фильтр не прочитался — возвращаем пустой конфиг (ничего не фильтруется)
            return new SystemAppsFilterConfig();
        }

        /// <summary>
        /// Создаёт файл фильтра из встроенной копии. Отказ записи (например,
        /// каталог установки только для чтения) — не поломка: приложение работает
        /// дальше на встроенной копии, файл пользователь создаст сам.
        /// </summary>
        private void SeedFile(string path, byte[] bytes)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Байты как есть: файл с BOM нормально открывается и в блокноте, и
                // в редакторе пользователя.
                File.WriteAllBytes(path, bytes);
                _logger.LogInformation("Создан файл фильтра системных приложений: {File}", path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning("Не удалось создать {File}: {Message}. Фильтр берётся из сборки.",
                    path, ex.Message);
            }
        }

        /// <summary>
        /// Снимает метку порядка байтов UTF-8: <see cref="JsonSerializer"/> не
        /// пропускает преамбулу для <c>byte[]</c> и падает на первом же байте. Файл
        /// фильтра сохранён с BOM, а чтение файла раньше шло через ReadAllText,
        /// который BOM снимал сам; теперь это делает метод — иначе собственная
        /// встроенная копия оказалась бы нечитаемой.
        /// </summary>
        private static byte[] WithoutBom(byte[] bytes) =>
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes[3..]
                : bytes;

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
