using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Runtime.InteropServices;

namespace SoundMeeter.Services;

/// <summary>
/// Гарантия одного работающего экземпляра (SM-D03).
///
/// Понадобилась вместе с треем и автозапуском. Пока окно было единственным
/// способом добраться до программы, второй запуск был заметен: пользователь видел
/// два окна. С треем первый экземпляр становится невидимым, и второй запускается
/// «молча» — а два WASAPI-клиента на одном устройстве расходятся по темпу и
/// дают треск и провалы звука.
///
/// Сделано через именованный мьютекс (не <c>FindWindow</c> по заголовку: он
/// совпадает у двух окон и не работает, когда окно скрыто в трее) плюс
/// именованное событие, которым второй экземпляр просит первый показать окно.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Имя объекта. <c>Local\</c> — намеренно: имена с обратной косой чертой
    /// общие для всех сеансов Windows, и пользователь, вошедший дважды
    /// (например, по RDP), не смог бы запустить вторую копию в другом сеансе.
    /// Идентификатор пользователя добавляем, чтобы два разных пользователя на
    /// одном компьютере не мешали друг другу.
    /// </summary>
    private static readonly string MutexName = BuildName("SoundMeeter.SingleInstance");

    private static readonly string EventName = BuildName("SoundMeeter.ShowWindow");

    private readonly ILogger _logger = AppLog.For<SingleInstanceGuard>();
    private readonly Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    /// <summary>true — этот процесс стал первым экземпляром.</summary>
    public bool IsPrimaryInstance { get; }

    /// <summary>Создан ли уже другой экземпляр (первый не нашёлся).</summary>
    public bool AlreadyRunning => !IsPrimaryInstance;

    public SingleInstanceGuard()
    {
        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущий экземпляр упал, не освободив мьютекс. Захват успешен —
            // это как раз наш случай, и считать его ошибкой нельзя.
            createdNew = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось создать мьютекс одиночного экземпляра: {Message}", ex.Message);
            IsPrimaryInstance = true;   // не блокируем запуск из-за проверки
            return;
        }

        IsPrimaryInstance = createdNew;
        if (!createdNew)
        {
            _logger.LogInformation("Обнаружен уже запущенный экземпляр — повторный запуск отменён");
        }
    }

    /// <summary>
    /// Подписаться на просьбу второго экземпляра показать окно. Вызывается
    /// первым экземпляром после создания окна.
    /// </summary>
    public void ListenForShowRequest(Action showWindow)
    {
        if (!IsPrimaryInstance) return;

        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _ = Task.Run(() =>
            {
                while (true)
                {
                    _showSignal.WaitOne();
                    try
                    {
                        showWindow();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось показать окно по запросу: {Message}", ex.Message);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось подписаться на сигнал показа окна: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Попросить уже запущенный экземпляр показать окно. Вызывается вторым
    /// экземпляром перед выходом. Возвращает false, если сигнал доставить не
    /// удалось (первый экземпляр ещё поднимается или уже закрывается).
    /// </summary>
    public static bool RequestShowWindow()
    {
        try
        {
            using var handle = EventWaitHandle.TryOpenExisting(EventName, out var existing)
                ? existing
                : null;
            if (handle is null) return false;

            handle.Set();
            return true;
        }
        catch (Exception)
        {
            // Сигнал — удобство, а не необходимость: если он не дошёл, второй
            // экземпляр просто тихо завершится, как и при любой другой ошибке.
            return false;
        }
    }

    public void Dispose()
    {
        _showSignal?.Dispose();
        _showSignal = null;

        if (_mutex is null) return;
        try
        {
            // Мьютекс отпускается только здесь: иначе второй экземпляр после
            // аварийного завершения первого сочл бы программу «ещё работающей».
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Не владеем — ничего и не отпускаем.
        }

        _mutex.Dispose();
    }

    private static string BuildName(string prefix) => $@"Local\{prefix}.{Environment.UserName}";
}