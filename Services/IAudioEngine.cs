using SoundMeeter.Models;

namespace SoundMeeter.Services;

public interface IAudioEngine : IDisposable
{
    IReadOnlyList<InputChannelModel> Inputs { get; }
    IReadOnlyList<OutputBusModel> Buses { get; }
    bool IsRunning { get; }

    /// <summary>Настройки MIDI-микшера (устройство + привязки контроллеров).</summary>
    MidiSettings Midi { get; set; }

    /// <summary>Каталог доступных устройств (для назначения источника стрипам).</summary>
    IReadOnlyList<DeviceInfo> Catalog { get; }

    /// <summary>Список устройств/каналов изменился (после RefreshDevices/назначения источника).</summary>
    event Action? ChannelsChanged;

    /// <summary>Состояние движка изменилось (запущен/остановлен).</summary>
    event Action? StateChanged;

    /// <summary>Перечитывает каталог WASAPI-устройств и проверяет доступность стрипов.</summary>
    void RefreshDevices();

    void Start();
    void Stop();

    /// <summary>Включает/выключает маршрут вход → шина (применяется на лету).</summary>
    void SetRoute(string inputId, string busId, bool enabled);

    /// <summary>Solo для входного стрипа: при активном solo несоло-входы замолкают.</summary>
    void SetInputSolo(string inputId, bool value);

    /// <summary>Solo для выходной шины: при активном solo несоло-шины замолкают.</summary>
    void SetOutputSolo(string busId, bool value);

    /// <summary>Добавляет новый входной стрип (без назначенного источника).</summary>
    void AddInput();

    void RemoveInput(string inputId);

    /// <summary>Добавляет новый выходной стрип (без назначенного устройства).</summary>
    void AddBus();

    void RemoveBus(string busId);

    /// <summary>Назначает источнику входного стрипа устройство (null — отключить).</summary>
    void SetInputSource(string inputId, string? deviceId);

    /// <summary>Назначает выходной шине устройство воспроизведения (null — отключить).</summary>
    void SetBusSource(string busId, string? deviceId);

    /// <summary>Создаёт стрипы по умолчанию, если списки пусты (первый запуск).</summary>
    void EnsureDefaultStrips();

    /// <summary>Снимок текущих настроек для сохранения.</summary>
    AppSettings CreateSnapshot();

    /// <summary>Накладывает сохранённый пресет (заменяет списки стрипов).</summary>
    void ApplyPreset(AppSettings preset);

    /// <summary>Инициализация стартового состояния (наводит записи роутинга).</summary>
    void EnsureRoutingTable();
}

/// <summary>Устройство в каталоге. IsMicrophone=true — устройство захвата (микрофон);
/// false — устройство воспроизведения (для входов как loopback, для шин как вывод).</summary>
public sealed record DeviceInfo(string DeviceId, string Name, bool IsMicrophone);