using SoundMeeter.Models;

namespace SoundMeeter.Services;

/// <summary>
/// Локальный сервер док-панели OBS: отдаёт страницу панели и гоняет по
/// WebSocket состояние микшера, принимая от панели команды (громкость,
/// mute/solo/mono). Слушает только loopback, наружу ничего не отдаёт.
///
/// Наружу панель отдаётся теми же файлами, что лежат во встроенных ресурсах
/// (Resources/obs-dock): у пользователя нет ни папки с HTML, ни настройки
/// OBS — есть один URL.
/// </summary>
public interface IObsDockServer : IDisposable
{
    /// <summary>Сервер слушает порт.</summary>
    bool IsRunning { get; }

    /// <summary>Порт, на котором сервер реально слушает (0 в настройках → выбранный свободный).</summary>
    int Port { get; }

    /// <summary>URL панели, который вставляется в док OBS.</summary>
    string Url { get; }

    /// <summary>Сколько панелей сейчас подключено.</summary>
    int ClientCount { get; }

    /// <summary>Команда от панели. Приходит из сетевого потока — применять в UI-потоке.</summary>
    event Action<ObsDockCommand>? CommandReceived;

    /// <summary>Подключение/отключение панели (для индикатора в окне настроек).</summary>
    event Action<int>? ClientsChanged;

    /// <summary>Поднимает сервер. Порт 0 — выбрать свободный. Повторный вызов с тем же портом — no-op.</summary>
    void Start(int port);

    /// <summary>Останавливает сервер и рвёт все подключения.</summary>
    void Stop();

    /// <summary>
    /// Рассылает состояние всем подключённым панелям. Вызывается из UI-потока
    /// по таймеру метров, поэтому читать состояние стрипов здесь безопасно.
    /// </summary>
    void Publish(in ObsDockState state);
}
