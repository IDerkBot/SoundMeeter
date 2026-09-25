using NAudio.CoreAudioApi;
using SoundMeeter.AudioPolicy;
using SoundMeeter.Models;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace SoundMeeter.Services
{
    public class AudioService : IAudioService, IDisposable
    {
        private readonly ISettingsService _settingsService;

        private readonly MMDeviceEnumerator _deviceEnumerator;
        private readonly Dictionary<uint, BitmapImage> _iconCache = new();
        private readonly object _cacheLock = new();
        private Timer? _refreshTimer;

        public event EventHandler? AudioDevicesChanged;
        public event EventHandler? AppsChanged;
        public string LastError { get; private set; } = string.Empty;

        public AudioService(ISettingsService settingsService)
        {
            _settingsService = settingsService;

            _deviceEnumerator = new MMDeviceEnumerator();
            _refreshTimer = new Timer(RefreshData, null, 0, 2000); // Обновление каждые 2 сек
        }

        private void RefreshData(object? state)
        {
            AudioDevicesChanged?.Invoke(this, EventArgs.Empty);
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<List<AudioDevice>> GetAudioOutputDevicesAsync()
        {
            return Task.Run(() =>
            {
                var devices = new List<AudioDevice>();
                var collection = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                // Получаем дефолтное устройство для пометки
                var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var defaultId = defaultDevice?.ID ?? "";

                foreach (var device in collection)
                {
                    devices.Add(new AudioDevice
                    {
                        Id = device.ID, // Этот ID напрямую скармливается AppRouter
                        Name = device.FriendlyName,
                        IconPath = string.Empty,
                        IsDefault = device.ID == defaultId
                    });
                }

                return devices;
            });
        }

        public Task<List<RunningApp>> GetRunningAppsWithAudioAsync()
        {
            return Task.Run(() =>
            {
                var apps = new List<RunningApp>();
                var seenProcessIds = new HashSet<uint>();
                var completedResets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failedResets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    try
                    {
                        var sessionManager = device.AudioSessionManager;
                        if (sessionManager == null) continue;

                        var sessions = sessionManager.Sessions;
                        if (sessions == null) continue;

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session == null) continue;

                            var processId = session.GetProcessID;
                            if (processId == 0) continue;

                            // Пропускаем дубликаты (одно приложение может иметь несколько сессий)
                            if (!seenProcessIds.Add(processId)) continue;

                            try
                            {
                                using var process = Process.GetProcessById((int)processId);

                                if (process.Id == 0 ||
                                    process.ProcessName.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                                    process.ProcessName.Equals("System", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                // ГЛАВНОЕ: Спрашиваем у AppRouter, куда реально перенаправлен этот процесс.
                                // Если вернется пустая строка, значит он играет на текущем устройстве (device.ID)

                                var exePath = process.MainModule?.FileName;
                                var rules = _settingsService.Settings.PersistentRoutes;
                                string actualDeviceId = device.ID;
                                var routedDeviceId = AppRouter.GetRouteEndpoint(processId);
                                bool hasExplicitRoute = !string.IsNullOrEmpty(routedDeviceId);
                                if (!string.IsNullOrEmpty(routedDeviceId)) actualDeviceId = routedDeviceId;

                                lock (rules)
                                {
                                    var matchedRule = rules.FirstOrDefault(r =>
                                        string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
                                    if (matchedRule != null)
                                    {
                                        bool reset = string.IsNullOrEmpty(matchedRule.DeviceId);
                                        // Для сброса всегда вызываем Set: пустой Get может означать ошибку чтения.
                                        bool applied = !reset && string.Equals(routedDeviceId, matchedRule.DeviceId,
                                            StringComparison.OrdinalIgnoreCase);
                                        applied = applied || AppRouter.SetRoute(processId, matchedRule.DeviceId) == 0;
                                        if (applied)
                                        {
                                            actualDeviceId = reset ? device.ID : matchedRule.DeviceId;
                                            hasExplicitRoute = !reset;
                                        }
                                        if (reset)
                                            (applied ? completedResets : failedResets).Add(matchedRule.ExecutablePath);
                                    }
                                }

                                var app = new RunningApp
                                {
                                    ProcessId = processId,
                                    Name = process.ProcessName,
                                    IconPath = processId.ToString(), // Ключ для кэша иконок
                                    CurrentDeviceId = actualDeviceId,
                                    HasAudioSession = true,
                                    HasExplicitRoute = hasExplicitRoute,
                                    ExecutablePath = exePath
                                };
                                apps.Add(app);
                            }
                            catch { /* Процесс мог завершиться */ }
                        }
                    }
                    catch { /* Устройство могло отключиться */ }
                }

                var pendingRules = _settingsService.Settings.PersistentRoutes;
                lock (pendingRules)
                {
                    if (pendingRules.RemoveAll(r => string.IsNullOrEmpty(r.DeviceId) &&
                            completedResets.Contains(r.ExecutablePath) && !failedResets.Contains(r.ExecutablePath)) > 0)
                        _settingsService.Save();
                }
                return apps;
            });
        }

        public BitmapImage? GetAppIcon(uint processId)
        {
            lock (_cacheLock)
            {
                if (_iconCache.TryGetValue(processId, out var cached))
                    return cached;
            }

            try
            {
                var process = Process.GetProcessById((int)processId);
                var fileName = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(fileName)) return null;

                using var icon = Icon.ExtractAssociatedIcon(fileName);
                if (icon == null) return null;

                var bitmap = ConvertIconToBitmapImage(icon);
                if (bitmap == null) return null;

                lock (_cacheLock)
                {
                    _iconCache[processId] = bitmap;
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapImage? ConvertIconToBitmapImage(Icon icon)
        {
            try
            {
                using var ms = new MemoryStream();
                icon.ToBitmap().Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze(); // Обязательно для многопоточности WPF
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public Task<(bool Success, string Error)> SetAppAudioDeviceAsync(uint processId, string deviceId)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Вызываем проверенный метод из SoundDeck. 
                    // Если deviceId пустой или null, AppRouter сам сбросит маршрут на системный по умолчанию.
                    int hr = AppRouter.SetRoute(processId, deviceId ?? "");

                    if (hr == 0) // S_OK
                    {
                        LastError = "";
                        return (true, string.Empty);
                    }

                    LastError = hr == AppRouter.ProcessNoAudio
                        ? $"Windows не применила маршрут для PID {processId} (0x{hr:X8}). Проверьте аудиосессию приложения и выбранное устройство."
                        : $"Ошибка COM: HRESULT 0x{hr:X8}";
                    return (false, LastError);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return (false, ex.Message);
                }
            });
        }

        public void Dispose()
        {
            _refreshTimer?.Dispose();
            lock (_cacheLock)
            {
                _iconCache.Clear();
            }
        }
    }
}
