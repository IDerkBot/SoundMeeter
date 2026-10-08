using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Чтение чата Twitch через EventSub WebSocket (SM-F01).
///
/// ПОЧЕМУ WEBSOCKET, А НЕ IRC. Twitch объявил IRC устаревшим и вывел из
/// эксплуатации (декомиссия 15 августа 2025), а чтение сообщений перенесено в
/// подписку <c>channel.chat.message</c> EventSub. Прямая реализация IRC здесь
/// означала бы работу с отключённым сервисом.
///
/// ЧТО ЗДЕСЬ СЛОЖНО И ПОЧЕМУ. Обычный «открыл сокет и читай» не работает, потому
/// что у протокола четыре особенности, и каждая ломает наивную реализацию:
///
/// * <b>Сессия.</b> Сервер присылает приветствие с идентификатором сессии, и
///   подписка создаётся уже с этим идентификатором. Без него Twitch закрывает
///   соединение через десять секунд, и тихо это не выглядит: сокет просто закрыт.
///
/// * <b>Keepalive.</b> Если не пришло ни события, ни keepalive за срок, указанный в
///   приветствии, соединение мертво. Молча ждать следующего сообщения нельзя: на
///   эфире это выглядело бы как «чат сдох», а сокет об этом не сообщает.
///
/// * <b>Переподключение.</b> Сервер заранее, за тридцать секунд до закрытия,
///   присылает адрес нового соединения. Пользоваться им надо немедленно, и старое
///   соединение нельзя закрывать, пока новое не отдало приветствие, — иначе между
///   ними будет зазор, и сообщения в него провалятся.
///
/// * <b>Отсутствие повтора.</b> Потерянное при обрыве не повторяется: это надо
///   предотвращать, а не догонять.
///
/// Разбор кадров вынесен в <see cref="EventSubFrameParser"/> и проверяется без
/// сокета; здесь только транспорт и его разрывы.
/// </summary>
public sealed class TwitchChatReader : ITwitchChatReader
{
    /// <summary>
    /// Адрес сессии EventSub. Значение берётся из <c>reconnect_url</c>, который
    /// сервер присылает при переподключении, и там оно используется ДОСЛОВНО:
    /// Twitch отклоняет адрес, изменённый нами (например, дописыванием параметра).
    /// </summary>
    private const string DefaultWebSocketUrl = "wss://eventsub.wss.twitch.tv/ws";

    /// <summary>Буфер приёма. Кадры небольшие, но с запасом на длинную реплику с эмоциями.</summary>
    private const int ReceiveBufferSize = 16 * 1024;

    /// <summary>Предел кадра. Кадр длиннее — это не сообщение чата, а мусор.</summary>
    private const int MaxFrameChars = ReceiveBufferSize * 8;

    /// <summary>
    /// Сколько ждать приветствия и ответа на подписку.
    ///
    /// Twitch требует подписки в десять секунд после приветствия, иначе соединение
    /// закрывает. Запас нужен, чтобы медленный сервер не выглядел поломкой.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Срок keepalive, который присылает Twitch, если в приветствии не указан иной.</summary>
    private const int DefaultKeepaliveSeconds = 10;

    /// <summary>
    /// Пауза перед повтором после обрыва.
    ///
    /// Без неё обрыв уходил бы в частый перебор: при упавшей сети это десятки
    /// попыток в секунду, а Twitch считает их и отвечает 429.
    /// </summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly Func<string> _webSocketUrl;
    private readonly ILogger _logger = AppLog.For<TwitchChatReader>();

    private CancellationTokenSource? _lifetime;
    private Thread? _worker;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Сессия открыта и подписка создана — сообщения приходят.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Причина последнего обрыва или отказа. Пусто — проблем нет.
    ///
    /// Показывается пользователю: «модуль не работает» без причины хуже, чем
    /// конкретный отказ, и по журналу стримеру приходилось бы догадываться.
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>Новое сообщение из чата. Прилетает из потока чтения, а не из UI.</summary>
    public event Action<TwitchChatMessage>? MessageReceived;

    /// <summary>Подписка отозвана: аккаунт удалён, доступ отозван или формат события больше не поддерживается.</summary>
    public event Action<string>? SubscriptionRevoked;

    /// <summary>Соединение поднялось или упало. true — читаем чат.</summary>
    public event Action<bool>? ConnectionChanged;

    public TwitchChatReader() : this(() => DefaultWebSocketUrl) { }

    /// <summary>
    /// Адрес сессии задаётся снаружи (внутренний конструктор): так проверка может
    /// подсунуть свой сервер и не ходить в Twitch.
    /// </summary>
    internal TwitchChatReader(Func<string> webSocketUrl) =>
        _webSocketUrl = webSocketUrl ?? throw new ArgumentNullException(nameof(webSocketUrl));

    /// <summary>
    /// Начать читать чат канала от имени аккаунта.
    ///
    /// false, если читать нечего: нет входа или нет идентификаторов. Это не
    /// ошибка, а «пока не настроено»: идентификатор канала узнаётся при первом
    /// подключении, и пользователь может задать канал позже.
    /// </summary>
    public bool Start(string broadcasterUserId, string readerUserId)
    {
        if (_disposed || _running) return false;
        if (string.IsNullOrWhiteSpace(broadcasterUserId) || string.IsNullOrWhiteSpace(readerUserId))
        {
            LastError = "не заданы идентификаторы канала или аккаунта";
            return false;
        }

        _running = true;
        LastError = "";
        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;

        _worker = new Thread(() => ListenAsync(broadcasterUserId, readerUserId, token)
            .GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "SoundMeeter.Twitch.Chat",
        };
        _worker.Start();

        _logger.LogInformation("Twitch: чтение чата запущено (канал {Channel}, чтение как {Reader})",
            broadcasterUserId, readerUserId);
        return true;
    }

    /// <summary>Перестать читать. Безопасно звать, когда уже остановлено.</summary>
    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _lifetime?.Cancel();

        Thread? worker = _worker;
        _worker = null;
        if (worker is { IsAlive: true })
        {
            try
            {
                worker.Join(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Поток чтения чата не завершился: {Message}", ex.Message);
            }
        }

        _lifetime?.Dispose();
        _lifetime = null;

        SetConnected(false);
        _logger.LogInformation("Twitch: чтение чата остановлено");
    }

    /// <summary>
    /// Внешний цикл: держит соединение и переподключается после обрыва.
    ///
    /// Один цикл на всё время работы, а не соединение на цикл: так гарантируется
    /// пауза между попытками и исключается метание сокетов при обрыве сети.
    ///
    /// <paramref name="pendingUrl"/> — адрес, присланный сервером для
    /// переподключения. Он не меняется: Twitch требует использовать его дословно.
    /// </summary>
    private async Task ListenAsync(string broadcasterUserId, string readerUserId, CancellationToken token)
    {
        string? pendingUrl = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                pendingUrl = await ListenOnceAsync(broadcasterUserId, readerUserId, pendingUrl, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Обрыв сети, отказ сервера, закрытие соединения — всё это попадает
                // сюда и НЕ должно ронять поток: иначе после первой неудачи модуль
                // молча перестал бы читать чат до перезапуска приложения.
                LastError = ex.Message;
                _logger.LogWarning(ex, "Twitch: соединение с чатом прервано: {Message}", ex.Message);

                // Адрес, выданный для переподключения, после обрыва не годится:
                // это одноразовый адрес конкретной сессии.
                pendingUrl = null;
            }
            finally
            {
                SetConnected(false);
            }

            if (token.IsCancellationRequested) break;

            try
            {
                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Одно соединение целиком: приветствие, подписка, чтение.
    ///
    /// Возвращает адрес для следующего соединения, если сервер потребовал
    /// переподключения, иначе null.
    /// </summary>
    private async Task<string?> ListenOnceAsync(string broadcasterUserId, string readerUserId,
        string? pendingUrl, CancellationToken token)
    {
        var url = pendingUrl ?? _webSocketUrl();

        using var socket = new ClientWebSocket();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(HandshakeTimeout);

        await socket.ConnectAsync(new Uri(url), handshake.Token).ConfigureAwait(false);

        var session = await ReadSessionAsync(socket, handshake.Token).ConfigureAwait(false);
        if (session is null)
        {
            LastError = "сервер не прислал идентификатор сессии";
            return null;
        }

        // Подписка на уже открытое соединение при переподключении не создаётся заново:
        // новый адрес наследует подписки старой сессии, и повтор упёрся бы в лимит.
        if (pendingUrl is null)
        {
            await SubscribeAsync(socket, session, broadcasterUserId, readerUserId, handshake.Token)
                .ConfigureAwait(false);
        }

        SetConnected(true);
        LastError = "";
        _logger.LogInformation("Twitch: соединение с чатом установлено");

        return await ReadLoopAsync(socket, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Чтение до отмены, обрыва или требования переподключения.
    ///
    /// Возвращает адрес для следующего соединения, если сервер его прислал.
    /// </summary>
    private async Task<string?> ReadLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        // Два срока keepalive: столько сервер вправе молчать. Пропуск большего
        // означает потерю соединения — Twitch не сообщает об этом, сокет остаётся
        // «живым», и без этой проверки чтение просто остановилось бы навсегда.
        var silenceLimit = TimeSpan.FromSeconds(DefaultKeepaliveSeconds * 2);

        while (!token.IsCancellationRequested)
        {
            var (kind, message, value) = await ReadFrameAsync(socket, silenceLimit, token)
                .ConfigureAwait(false);

            switch (kind)
            {
                case EventSubFrame.ChatMessage when message is not null:
                    RaiseMessage(message);
                    break;

                case EventSubFrame.Reconnect:
                    _logger.LogInformation("Twitch: сервер требует переподключения");
                    return value;

                case EventSubFrame.Revocation:
                    LastError = value ?? "подписка отозвана";
                    _logger.LogWarning("Twitch: подписка отозвана ({Status})", value ?? "причина не указана");
                    RaiseRevoked(LastError);
                    return null;

                case EventSubFrame.Keepalive:
                case EventSubFrame.Unknown:
                case EventSubFrame.Welcome:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Создать подписку на сообщения чата в открытой сессии.
    ///
    /// Подписка уходит прямо в сокет: у EventSub WebSocket доставка именно такая,
    /// отдельного HTTP-запроса на создание нет.
    /// </summary>
    private async Task SubscribeAsync(ClientWebSocket socket, string sessionId,
        string broadcasterUserId, string readerUserId, CancellationToken token)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = "channel.chat.message",
            ["version"] = "1",
            ["condition"] = new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterUserId,
                ["user_id"] = readerUserId,
            },
            ["transport"] = new Dictionary<string, string>
            {
                ["method"] = "websocket",
                ["session_id"] = sessionId,
            },
        };

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);

        // Сервер десять секунд ждёт подписку и молча закрывает соединение без неё.
        // Таймер нужен, чтобы приложение не висело, если сокет перестанет отвечать.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HandshakeTimeout);

        await socket.SendAsync(body, WebSocketMessageType.Text, endOfMessage: true, timeout.Token)
            .ConfigureAwait(false);

        _logger.LogInformation("Twitch: подписка на сообщения чата создана");
    }

    /// <summary>Прочитать приветствие и достать из него идентификатор сессии.</summary>
    private async Task<string?> ReadSessionAsync(ClientWebSocket socket, CancellationToken token)
    {
        var (kind, _, value) = await ReadFrameAsync(socket, silenceLimit: null, token)
            .ConfigureAwait(false);

        return kind == EventSubFrame.Welcome ? value : null;
    }

    /// <summary>
    /// Прочитать один кадр.
    ///
    /// Молчание дольше срока keepalive считается обрывом: сервер обязан присылать
    /// keepalive, и если не прислал — соединения больше нет, хотя сокет об этом
    /// не сообщает. Без этой проверки модуль просто перестал бы получать
    /// сообщения, и пользователь увидел бы «чат молчит» без всякой причины.
    ///
    /// <paramref name="silenceLimit"/> null — ждать без ограничения. Так читается
    /// приветствие: ограничивать его сроком keepalive нельзя, потому что keepalive
    /// приходит после него, и ожидание иначе всегда заканчивалось бы ошибкой.
    /// </summary>
    private async Task<(EventSubFrame, TwitchChatMessage?, string?)> ReadFrameAsync(
        ClientWebSocket socket, TimeSpan? silenceLimit, CancellationToken token)
    {
        var buffer = new byte[ReceiveBufferSize];
        var builder = new StringBuilder();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (silenceLimit is { } limit) linked.CancelAfter(limit);

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException($"сервер закрыл соединение (код {socket.CloseStatus})");
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage) break;

                // Кадр длиннее буфера продолжаем копить, иначе JSON пришёл бы
                // обрезанным и не разобрался. Бесконечно растущий кадр — мусор.
                if (builder.Length > MaxFrameChars) throw new IOException("кадр слишком велик");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && silenceLimit is { } waited)
        {
            throw new IOException(
                $"сервер не прислал ничего за {waited.TotalSeconds:F0} с — соединение потеряно");
        }

        return EventSubFrameParser.Parse(builder.ToString());
    }

    /// <summary>
    /// Раздать сообщение подписчикам.
    ///
    /// Ошибка подписчика не должна ронять чтение: подписчик здесь один (модуль
    /// озвучки), и его сбой иначе остановил бы весь чат молча.
    /// </summary>
    private void RaiseMessage(TwitchChatMessage message)
    {
        try
        {
            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitch: обработчик сообщения упал: {Message}", ex.Message);
        }
    }

    private void RaiseRevoked(string reason)
    {
        try
        {
            SubscriptionRevoked?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitch: обработчик отзыва подписки упал: {Message}", ex.Message);
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;

        IsConnected = value;
        try
        {
            ConnectionChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitch: обработчик состояния упал: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        MessageReceived = null;
        SubscriptionRevoked = null;
        ConnectionChanged = null;
    }
}