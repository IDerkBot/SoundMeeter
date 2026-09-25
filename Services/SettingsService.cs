using SoundMeeter.Models;
using System.IO;
using System.Text.Json;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Персистентное хранилище настроек. Реализует <see cref="ISettingsService"/> —
    /// контракт, который ожидают MainViewModel и AudioService (портирован из AudioRouter):
    /// единый снимок настроек в <see cref="Settings"/> + <see cref="Save"/>.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private readonly IAudioEngine _engine;
        private readonly object _writeLock = new();

        public SettingsService(IAudioEngine engine) => _engine = engine;

        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundMeeter",
            "settings.json");

        /// <summary>Текущий снимок настроек (выставляется в App.OnStartup после Load).</summary>
        public AppSettings Settings { get; set; } = new();

        /// <summary>Синхронное сохранение текущего снимка <see cref="Settings"/>.</summary>
        public void Save() => SaveSync(_engine.CreateSnapshot());

        /// <summary>
        /// Синхронное сохранение. Используется при закрытии окна: вызов из
        /// UI-потока блокирующего async-апдейта (GetAwaiter().GetResult())
        /// приводит к deadlock'у — continuation async-метода пытается вернуться
        /// в UI-поток, который уже заблокирован. Файл настроек небольшой,
        /// поэтому синхронная запись безопасна и проста.
        /// </summary>
        public void SaveSync(AppSettings settings)
        {
            var rules = Settings.PersistentRoutes;
            lock (rules)
            lock (_writeLock)
            {
                // Снимок движка не содержит правил приложений. Дополняем его актуальными данными.
                settings.PersistentRoutes = rules.Select(r => new DeviceRouteRule
                {
                    ExecutablePath = r.ExecutablePath, DeviceId = r.DeviceId,
                    AppName = r.AppName, IconPath = r.IconPath
                }).ToList();
                settings.HiddenDeviceIds = Settings.HiddenDeviceIds.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
        }

        public Task SaveAsync(AppSettings settings) => Task.Run(() => SaveSync(settings));

        public async Task<AppSettings?> LoadAsync()
        {
            if (!File.Exists(_path)) return null;
            try
            {
                await using var fs = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<AppSettings>(fs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load settings failed: {ex.Message}");
                return null;
            }
        }
    }
}
