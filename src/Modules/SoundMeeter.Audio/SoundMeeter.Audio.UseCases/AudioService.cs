using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using SoundMeeter.AudioPolicy;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Diagnostics;
using System.IO;

namespace SoundMeeter.Services
{
    public class AudioService : IAudioService, IDisposable
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger = AppLog.For<AudioService>();

        private readonly MMDeviceEnumerator _deviceEnumerator;
        private Timer? _refreshTimer;

        /// <summary>
        /// Пути приложений, о сбросе которых уже сообщили: опрос идёт каждые две
        /// секунды, и без этого журнал забивался бы одной и той же строкой.
        /// </summary>
        private readonly HashSet<string> _reportedResetFailures = new(StringComparer.OrdinalIgnoreCase);

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

                // УТЕЧКА (SM-A08): MMDevice — это COM-обёртка над IMMDevice, и
                // финализатора у неё НЕТ: без явного Dispose() ссылка на
                // устройство не отпускается никогда. Метод зовётся из таймера
                // каждые 2 секунды, поэтому за сутки утекали сотни тысяч
                // COM-объектов, и процесс разрастался до гигабайтов.
                // Устройство нужно только на время чтения ID и имени — всё, что
                // живёт дольше, получает копию строк.
                using var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                string defaultId = defaultDevice?.ID ?? "";

                foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        devices.Add(new AudioDevice
                        {
                            Id = device.ID, // Этот ID напрямую скармливается AppRouter
                            Name = device.FriendlyName,
                            IconPath = string.Empty,
                            IsDefault = device.ID == defaultId
                        });
                    }
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

                foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    // Тот же случай, что и в GetAudioOutputDevicesAsync (SM-A08):
                    // MMDevice без финализатора, а обход идёт каждые 2 секунды.
                    // AudioSessionManager — тоже COM-объект, и он держит
                    // коллекцию сессий, поэтому освобождается вместе с устройством.
                    using var deviceScope = device;
                    try
                    {
                        using var sessionManager = device.AudioSessionManager;
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

                foreach (var path in failedResets)
                {
                    if (!_reportedResetFailures.Add(path)) continue;
                    _logger.LogWarning("Не удалось сбросить маршрут приложения {App} на системный вывод", path);
                }

                return apps;
            });
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
                        _logger.LogInformation("Маршрут PID {Pid} -> {Device}",
                            processId, string.IsNullOrEmpty(deviceId) ? "<системный вывод>" : deviceId);
                        return (true, string.Empty);
                    }

                    LastError = hr == AppRouter.ProcessNoAudio
                        ? Loc.Get("Sm.Routing.NotApplied", processId, hr)
                        : Loc.Get("Sm.Routing.ComFailed", hr);
                    _logger.LogWarning("Маршрутизация PID {Pid} не выполнена, HRESULT=0x{HResult:X8}", processId, hr);
                    return (false, LastError);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _logger.LogError(ex, "Маршрутизация PID {Pid} упала: {Message}", processId, ex.Message);
                    return (false, ex.Message);
                }
            });
        }

        public void Dispose()
        {
            _refreshTimer?.Dispose();

            // Сам перечислитель — тоже COM-объект (SM-A08). Без этого он и его
            // внутренние ссылки на перечисление устройств живут до конца процесса.
            try
            {
                _deviceEnumerator.Dispose();
            }
            catch
            {
                // Устройство могло исчезнуть при выключении — гасим молча.
            }
        }
    }
}