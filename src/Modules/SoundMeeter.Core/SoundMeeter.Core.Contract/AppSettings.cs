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

    /// <summary>
    /// Язык интерфейса: пустая строка — «как в системе», иначе "en"/"ru" (SM-C07).
    /// Поле необязательное: в settings.json, написанном прошлыми сборками, его нет,
    /// и это равносильно «язык системы», поэтому миграция схемы его не трогает.
    /// </summary>
    public string Language { get; set; } = "";

    public List<DeviceRouteRule> PersistentRoutes { get; set; } = new();
    public bool EngineWasRunning { get; set; }
    public List<InputChannelModel> Inputs { get; set; } = new();
    public List<OutputBusModel> Outputs { get; set; } = new();

    /// <summary>
    /// DeviceId устройств, чьи стрипы пользователь удалил («скрыл»).
    /// Должны переживать перезапуск: вернуть из пресета канал, который
    /// пользователь снёс, нельзя. На состав списка устройств не влияют —
    /// стрипы создаёт только пользователь.
    /// </summary>
    public List<string> RemovedDeviceIds { get; set; } = new();

    /// <summary>DeviceId устройств, скрытых пользователем (кнопка «скрыть»/перенос приложений).</summary>
    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>MIDI-микшер: устройство ввода и привязки контроллеров к стрипам.</summary>
    public MidiSettings Midi { get; set; } = new();

    /// <summary>Док-панель в OBS: порт сервера, автостарт и список каналов.</summary>
    public ObsDockSettings ObsDock { get; set; } = new();

    /// <summary>Модуль синтеза речи: включён ли, куда отдавать голос и как его читать (SM-E01).</summary>
    public TextToSpeechSettings TextToSpeech { get; set; } = new();

    /// <summary>
    /// Интеграция с Twitch: вход, канал для чтения чата и показ статуса трансляции
    /// (SM-F01). Отдельный блок от <see cref="TextToSpeech"/>, потому что чат читается
    /// не только ради озвучки.
    /// </summary>
    public TwitchSettings Twitch { get; set; } = new();

    /// <summary>
    /// Показывать значок в системном лотке (SM-D02). По умолчанию включено:
    /// микшер уходит на сверху окна и должен оставаться достижимым, а
    /// закрытие окна сворачивает его в трей вместо выхода.
    /// </summary>
    public bool TrayEnabled { get; set; } = true;

    /// <summary>
    /// Запускать приложение вместе с Windows (SM-D01).
    ///
    /// По умолчанию выключено: автозапуск меняет состояние системы за пределами
    /// приложения, и включать его без явного решения пользователя нельзя. На
    ///стройка и состояние реестра могут разойтись (файл настроек скопирован на
    /// другой компьютер), поэтому фактическое состояние читается из реестра, а
    /// это поле хранит только пожелание.
    /// </summary>
    public bool RunAtStartup { get; set; }
}