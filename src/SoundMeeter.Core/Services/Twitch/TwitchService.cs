using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using SoundMeeter.Services.TextToSpeech;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Интеграция с Twitch целиком (SM-F01): вход, чтение чата, статус трансляции.
///
/// Здесь и только здесь решается, когда подключаться и когда отключаться. Всё
/// остальное уже написано и проверено по отдельности: <see cref="TwitchAuthService"/>
/// умеет входить, <see cref="TwitchApiClient"/> — спрашивать, <see cref="TwitchChatReader"/>
/// — читать. Смешивать их в одном классе означало бы, что правка любой части тянет
/// за собой остальные и не проверяется по отдельности.
///
/// ЧТО ЭТОТ КЛАСС ДАЁТ МОДУЛЮ ОЗВУЧКИ. Ровно одно: сообщения чата через
/// <see cref="MessageReceived"/> и старт/стоп по настройкам. Разбор команд
/// (<c>!tts</c>, <c>!ttsvoice</c>), очередь, дроссель и синтез остаются в модуле —
/// он их уже написан и покрыт проверками, и переписывать их под Twitch незачем.
/// </summary>
public sealed class TwitchService : ITwitchState, IDisposable
{
    private readonly ITwitchAuth _auth;
    private readonly ITwitchApi _api;
    private readonly ITwitchChatReader _chat;
    private readonly ISettingsService _settings;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger _logger = AppLog.For<TwitchService>();

    private readonly object _sync = new();

    private CancellationTokenSource? _statusPolling;
    private Thread? _pollWorker;
    private bool _disposed;
    private bool _statusTimerRunning;

    /// <summary>Что сейчас известно о трансляции.</summary>
    private volatile TwitchStreamState _streamState = TwitchStreamState.Unknown;

    /// <summary>Новое сообщение из чата. Прилетает из потока чтения, не из UI.</summary>
    public event Action<TtsChatMessage>? MessageReceived;

    /// <summary>Состояние изменилось: чат подключился, отвалился, сменился канал.</summary>
    public event Action? StatusChanged;

    /// <summary>Идёт ли трансляция.</summary>
    public TwitchStreamState StreamState => _streamState;

    /// <summary>Настройки интеграции.</summary>
    public TwitchSettings Settings => _settings.Settings.Twitch;

    /// <summary>Чат подключён и сообщения приходят.</summary>
    public bool IsChatConnected => _chat.IsConnected;

    /// <summary>Вход выполнен. Токен есть и им можно пользоваться.</summary>
    public bool IsAuthorized => _auth.IsAuthorized;

    /// <summary>Логин, под которым выполнен вход.</summary>
    public string AuthorizedLogin => _auth.AuthorizedLogin;

    /// <summary>Причина последней неудачи подключения. Пусто — проблем нет.</summary>
    public string LastError => _chat.LastError;

    /// <summary>Приложение собрано: в коде есть client ID. Без него вход невозможен.</summary>
    public static bool IsConfigured => TwitchAuthService.IsConfigured;

    public TwitchService(
        ITwitchAuth auth,
        ITwitchApi api,
        ITwitchChatReader chat,
        ISettingsService settings,
        IDispatcherService dispatcher)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _chat.MessageReceived += OnChatMessage;
        _chat.ConnectionChanged += OnChatConnectionChanged;
    }

    /// <summary>
    /// Поднять интеграцию при старте приложения.
    ///
    /// Молча выходит, если вход не выполнен или канал не выбран: это нормальное
    /// состояние для большинства пользователей, а не ошибка, о которой надо
    /// сообщать в журнал при каждом запуске.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return;

        // Токен восстанавливается всегда, даже если чат выключен: вход должен
        // сохраняться между перезапусками, а не требовать повторного подтверждения.
        bool authorized = await _auth.RestoreAsync(cancellationToken).ConfigureAwait(false);
        if (!authorized) return;

        RecordLogin(_auth.AuthorizedLogin);
        ApplySettings();
    }

    /// <summary>
    /// Начать вход: показать код и дождаться подтверждения.
    ///
    /// Возвращает код, который пользователь вводит на странице Twitch. Дальше
    /// <paramref name="challenge"/> надо опрашивать по
    /// <see cref="DeviceCodeChallenge.PollInterval"/> — чаще Twitch не отвечает.
    /// </summary>
    public Task<DeviceCodeChallenge?> BeginLoginAsync(CancellationToken cancellationToken = default) =>
        _auth.BeginLoginAsync(cancellationToken);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    public async Task<DeviceAuthResult> ContinueLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        var result = await _auth.ContinueLoginAsync(challenge, cancellationToken).ConfigureAwait(false);

        // Вход состоялся — сразу поднимаем чат, иначе пользователю пришлось бы
        // после подтверждения ещё что-то включать.
        if (result.Status == DeviceAuthStatus.Authorized)
        {
            RecordLogin(result.Token!.Login);
            ApplySettings();
        }

        return result;
    }

    /// <summary>Выйти из аккаунта и остановить всё, что от него зависит.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        StopChat();
        StopStatusPolling();

        await _auth.LogoutAsync(cancellationToken).ConfigureAwait(false);

        // Признак входа — поле в настройках, а не в файле токена, поэтому его надо
        // снять явно: иначе окно показывало бы «вошли» при отсутствии токена.
        RecordLogin("");

        _streamState = TwitchStreamState.Unknown;
        RaiseStatusChanged();
        _logger.LogInformation("Twitch: выход выполнен");
    }

    /// <summary>
    /// Применить настройки: переподключить чат и опрос статуса под то, что выбрал
    /// пользователь.
    ///
    /// Вызывается и при нажатии «Применить» в окне, и после входа. Канал, читающий
    /// чат, при смене обязан переподключиться: подписка адресована конкретному
    /// каналу, и старая молча перестала бы приносить сообщения.
    /// </summary>
    public void ApplySettings()
    {
        var settings = Settings;

        StopChat();
        StopStatusPolling();

        if (!settings.Enabled || !IsAuthorized)
        {
            RaiseStatusChanged();
            return;
        }

        // Идентификатор канала у Twitch меняется вместе с логином? Нет: он постоянен.
        // Кэш нужен потому, что каждый раз спрашивать его — лишний запрос при
        // каждом переподключении, а канал пользователя не переименовывает.
        var channelUserId = settings.ChannelUserId;
        if (string.IsNullOrEmpty(channelUserId))
        {
            channelUserId = ResolveChannelUserId();
            if (string.IsNullOrEmpty(channelUserId))
            {
                RaiseStatusChanged();
                return;
            }
        }

        string readerUserId = _auth.UserId;
        if (!_chat.Start(channelUserId, readerUserId))
        {
            _logger.LogDebug("Twitch: подключение к чату не начато — {Reason}", _chat.LastError);
        }

        if (settings.ShowStreamStatus) StartStatusPolling();

        RaiseStatusChanged();
    }

    /// <summary>
    /// Узнать идентификатор канала и запомнить его.
    ///
    /// Кэшируется в настройках, потому что подписка адресуется идентификатором, а
    /// логином — нет: каждый перезапуск приложения иначе делал бы лишний запрос.
    /// </summary>
    private string ResolveChannelUserId()
    {
        string login = Settings.NormalizedChannel();
        if (login.Length == 0) return "";

        var userId = _api.ResolveChannelUserIdAsync(login).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Twitch: канал «{Channel}» не найден", login);
            return "";
        }

        var settings = Settings;
        settings.ChannelUserId = userId;
        _settings.Save();
        return userId;
    }

    /// <summary>Опрос статуса трансляции в фоне.</summary>
    private void StartStatusPolling()
    {
        lock (_sync)
        {
            if (_statusTimerRunning) return;

            _statusTimerRunning = true;
            _statusPolling = new CancellationTokenSource();
            var token = _statusPolling.Token;

            _pollWorker = new Thread(() => PollStreamStatus(token))
            {
                IsBackground = true,
                Name = "SoundMeeter.Twitch.Status",
            };
            _pollWorker.Start();
        }
    }

    private void StopStatusPolling()
    {
        Thread? worker;

        lock (_sync)
        {
            if (!_statusTimerRunning) return;

            _statusTimerRunning = false;
            _statusPolling?.Cancel();
            worker = _pollWorker;
            _pollWorker = null;
        }

        if (worker is { IsAlive: true })
        {
            try
            {
                worker.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Поток опроса статуса не завершился: {Message}", ex.Message);
            }
        }

        _statusPolling?.Dispose();
        _statusPolling = null;
    }

    /// <summary>
    /// Опрос статуса трансляции.
    ///
    /// Сначала опрос сразу, а не через интервал: пользователь включил показ статуса
    /// и ждёт ответа, а ждать полминуты после нажатия «Применить» невежливо.
    /// </summary>
    private void PollStreamStatus(CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(TwitchSettings.Limits.StreamStatusPollMs);

        while (!token.IsCancellationRequested)
        {
            try
            {
                UpdateStreamStatus();
            }
            catch (Exception ex)
            {
                // Фоновый опрос каждые полминуты: исключение здесь уронило бы поток,
                // и индикатор замер бы навсегда без объяснения.
                _logger.LogDebug(ex, "Twitch: опрос статуса трансляции не удался: {Message}", ex.Message);
            }

            try
            {
                token.WaitHandle.WaitOne(interval);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Спросить, идёт ли трансляция, и отметить изменение.
    ///
    /// null (сеть недоступна) состояние НЕ меняет: обрыв сети не должен выглядеть
    /// как завершившаяся трансляция — пользователь увидел бы «не в эфире» на
    /// своей же трансляции.
    /// </summary>
    private void UpdateStreamStatus()
    {
        string login = Settings.NormalizedChannel();
        if (login.Length == 0) return;

        bool? live = _api.IsLiveAsync(login).GetAwaiter().GetResult();
        if (live is null) return;

        var state = live.Value ? TwitchStreamState.Live : TwitchStreamState.Offline;
        if (state == _streamState) return;

        _streamState = state;
        RaiseStatusChanged();

        _logger.LogInformation("Twitch: трансляция «{Channel}» {State}", login,
            state == TwitchStreamState.Live ? "началась" : "закончилась");
    }

    private void StopChat() => _chat.Stop();

    private void OnChatMessage(TwitchChatMessage message)
    {
        try
        {
            MessageReceived?.Invoke(new TtsChatMessage(message.User, message.Text));
        }
        catch (Exception ex)
        {
            // Ошибка подписчика не должна ронять чтение чата: иначе один сбойный
            // обработчик остановил бы весь чат молча.
            _logger.LogError(ex, "Twitch: обработчик сообщения упал: {Message}", ex.Message);
        }
    }

    private void OnChatConnectionChanged(bool connected)
    {
        _logger.LogInformation("Twitch: чат {State}", connected ? "подключён" : "отключён");
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            // Событие приходит из фоновых потоков, а окно настроек читает состояние
            // из UI: без перехода на поток интерфейса это было бы обращением к
            // элементам из чужого потока.
            _dispatcher.Post(() =>
            {
                try
                {
                    StatusChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Twitch: обработчик состояния упал: {Message}", ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitch: не удалось передать состояние в интерфейс: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Запомнить, под кем выполнен вход.
    ///
    /// Признак входа лежит в настройках, а токен — в отдельном файле: показывать
    /// «вошли» нужно по обоим условиям сразу. Пустой логин означает «выход».
    /// </summary>
    private void RecordLogin(string login)
    {
        var settings = Settings;
        string normalized = (login ?? "").Trim();

        if (string.Equals(settings.AuthorizedLogin, normalized, StringComparison.Ordinal)) return;

        settings.AuthorizedLogin = normalized;
        _settings.Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopStatusPolling();
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionChanged -= OnChatConnectionChanged;
        _chat.Dispose();

        MessageReceived = null;
        StatusChanged = null;
    }
}

/// <summary>Что известно о трансляции канала.</summary>
public enum TwitchStreamState
{
    /// <summary>Неизвестно: сеть недоступна или статус ещё не спрошен.</summary>
    Unknown,

    /// <summary>Трансляция не идёт.</summary>
    Offline,

    /// <summary>Идёт трансляция.</summary>
    Live,
}