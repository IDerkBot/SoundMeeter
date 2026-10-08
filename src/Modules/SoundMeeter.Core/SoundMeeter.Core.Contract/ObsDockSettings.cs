namespace SoundMeeter.Models;

/// <summary>
/// Настройки док-панели SoundMeeter в OBS. Док — это браузерная страница,
/// которую приложение само отдаёт по http://127.0.0.1:Port, а OBS открывает
/// как Browser Dock (View → Docks). Никаких плагинов OBS не требуется.
///
/// Хранится в settings.json рядом с остальными настройками, поэтому переживает
/// перезапуск приложения.
/// </summary>
public class ObsDockSettings
{
    /// <summary>Сервер дока поднимать при старте приложения.</summary>
    public bool Enabled { get; set; }

    /// <summary>Порт локального сервера. 0 — выбрать свободный автоматически.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Показывать в доке все входные стрипы. Список каналов ниже добавляется
    /// к этому перечню, поэтому «все входы» и пару нужных выходов можно
    /// получить одним движением.
    /// </summary>
    public bool ShowAllInputs { get; set; } = true;

    /// <summary>Показывать в доке все выходные шины.</summary>
    public bool ShowAllOutputs { get; set; }

    /// <summary>Явно выбранные каналы (дополняют ShowAllInputs/ShowAllOutputs).</summary>
    public List<ObsDockChannelRef> Channels { get; set; } = new();

    /// <summary>Порт по умолчанию (тот же, что в прошлых сборках не использовался).</summary>
    public const int DefaultPort = 17954;

    /// <summary>
    /// Порт, на котором реально работает сервер: настройка, но с подстановкой
    /// значения по умолчанию, если в файле лежит мусор.
    /// </summary>
    public int EffectivePort => Port is > 0 and < 65536 ? Port : DefaultPort;
}

/// <summary>
/// Ссылка на стрип микшера в списке каналов дока. Хранится и Id стрипа, и
/// DeviceId устройства: движок при повторном обнаружении устройств пересоздаёт
/// стрип с новым Id, и по одному лишь Id выбранный канал молча исчез бы из дока.
/// Привязка к устройству переживает пересоздание стрипа.
/// </summary>
public class ObsDockChannelRef
{
    /// <summary>Id стрипа микшера (InputChannelModel.Id / OutputBusModel.Id).</summary>
    public string StripId { get; set; } = "";

    /// <summary>DeviceId устройства стрипа (может быть пустым у неназначенного стрипа).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Kind входа (in) или выхода (out).</summary>
    public string Kind { get; set; } = ObsDockChannels.Input;

    public ObsDockChannelRef() { }

    public ObsDockChannelRef(string stripId, string deviceId, string kind)
    {
        StripId = stripId;
        DeviceId = deviceId;
        Kind = kind;
    }

    public ObsDockChannelRef Clone() => new(StripId, DeviceId, Kind);
}

/// <summary>Константы вида канала (вход/выход) — общие для настроек и протокола дока.</summary>
public static class ObsDockChannels
{
    public const string Input = "in";
    public const string Output = "out";
}
