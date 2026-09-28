namespace SoundMeeter.Models;

/// <summary>
/// Снимок настроек для сохранения/восстановления.
/// Роутинг хранится по DeviceId, поэтому переживает перезапуск приложения.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Версия схемы файла настроек (SM-A05). 0 — формат до введения версионирования
    /// (то есть любой settings.json, написанный прошлыми сборками).
    /// Текущая версия: <see cref="SettingsMigrator.CurrentSchemaVersion"/>.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Уровень детализации журнала (Trace/Debug/Information/Warning/Error).</summary>
    public string LogLevel { get; set; } = "Information";

    public List<DeviceRouteRule> PersistentRoutes { get; set; } = new();
    public bool EngineWasRunning { get; set; }
    public List<InputChannelModel> Inputs { get; set; } = new();
    public List<OutputBusModel> Outputs { get; set; } = new();

    /// <summary>
    /// DeviceId устройств, чьи стрипы пользователь удалил («скрыл»).
    /// Должны переживать перезапуск и обновление списка устройств —
    /// иначе AdoptDevicesUnlocked снова создаст для них стрипы.
    /// </summary>
    public List<string> RemovedDeviceIds { get; set; } = new();

    /// <summary>DeviceId устройств, скрытых пользователем (кнопка «скрыть»/перенос приложений).</summary>
    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>MIDI-микшер: устройство ввода и привязки контроллеров к стрипам.</summary>
    public MidiSettings Midi { get; set; } = new();

    /// <summary>Док-панель в OBS: порт сервера, автостарт и список каналов.</summary>
    public ObsDockSettings ObsDock { get; set; } = new();
}