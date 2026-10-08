using SoundMeeter.Models;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Вход в Twitch и хранение токенов (SM-F01).
///
/// Интерфейс существует ради ОДНОЙ причины: <see cref="TwitchService"/> должен
/// проверяться без сети. Иначе проверка интеграции требовала бы настоящего
/// аккаунта Twitch, настоящего канала и настоящей трансляции — то есть её нельзя
/// было бы запустить ни на чужой машине, ни в сборке по расписанию.
///
/// Всё остальное (формат ответов, разбор, хранение) проверяется на конкретных
/// классах, а здесь только то, что сервис реально вызывает.
/// </summary>
public interface ITwitchAuth
{
    /// <summary>Приложение собрано: в коде есть client ID.</summary>
    static bool IsConfigured => true;

    /// <summary>Токен есть, и им можно пользоваться.</summary>
    bool IsAuthorized { get; }

    /// <summary>Логин, под которым выполнен вход. Пусто — вход не выполнен.</summary>
    string AuthorizedLogin { get; }

    /// <summary>Идентификатор аккаунта по токену. Пусто — вход не выполнен.</summary>
    string UserId { get; }

    /// <summary>Текущий токен для обращения к API. Пусто — вход не выполнен.</summary>
    string? AccessToken { get; }

    /// <summary>
    /// Восстановить вход из сохранённого токена, обновив его при необходимости.
    ///
    /// Вызывается при старте приложения: ждать, пока токен истечёт, означало бы
    /// обнаружить это уже на первом сообщении из чата — то есть на эфире.
    /// </summary>
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Запросить код входа. null — приложение не настроено или сервер отказал.</summary>
    Task<DeviceCodeChallenge?> BeginLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    Task<DeviceAuthResult> ContinueLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default);

    /// <summary>Выйти: отозвать токены у Twitch и забыть их локально.</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// То, что окну настроек нужно знать о текущем состоянии интеграции (SM-F01).
///
/// Отдельный контракт от <see cref="ITwitchService"/>: окно показывает состояние, а не
/// управляет подключением. Смешав эти две роли в одном интерфейсе, окно получило бы
/// методы, которых ему не нужно, и проверку окна пришлось бы поднимать с настоящим
/// сервисом — вместе с его сетью и потоками.
/// </summary>
public interface ITwitchState
{
    /// <summary>Вход выполнен: токен есть и им можно пользоваться.</summary>
    bool IsAuthorized { get; }

    /// <summary>Логин, под которым выполнен вход. Пусто — вход не выполнен.</summary>
    string AuthorizedLogin { get; }

    /// <summary>Чат подключён и сообщения приходят.</summary>
    bool IsChatConnected { get; }

    /// <summary>Что известно о трансляции канала.</summary>
    TwitchStreamState StreamState { get; }

    /// <summary>Настройки интеграции.</summary>
    TwitchSettings Settings { get; }

    /// <summary>Состояние изменилось.</summary>
    event Action? StatusChanged;
}

/// <summary>
/// Разговор с Helix API Twitch (SM-F01).
///
/// Интерфейс — по той же причине, что и <see cref="ITwitchAuth"/>: проверки
/// интеграции не должны ходить в сеть за статусом трансляции.
/// </summary>
public interface ITwitchApi
{
    /// <summary>
    /// Идёт ли трансляция. null — неизвестно: сеть недоступна или канала нет.
    ///
    /// Различать «не в эфире» и «неизвестно» обязательно, и это не формальность:
    /// обрыв сети не должен выглядеть на эфире как завершившаяся трансляция.
    /// </summary>
    Task<bool?> IsLiveAsync(string channelLogin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Идентификатор канала по логину. null — канала нет, логина нет или сети нет.
    ///
    /// Нужен потому, что подписка на чат адресуется идентификатором, а не логином.
    /// </summary>
    Task<string?> ResolveChannelUserIdAsync(string channelLogin, CancellationToken cancellationToken = default);
}

/// <summary>
/// Чтение чата Twitch (SM-F01).
///
/// Отдельный интерфейс, потому что у чтения есть собственная жизнь: свой поток,
/// свои обрывы и переподключения. Встроенная проверка этого класса потребовала бы
/// сервера, говорящего по протоколу EventSub, и поднятие такого сервера ради
/// проверки дороже самой проверки.
/// </summary>
public interface ITwitchChatReader : IDisposable
{
    /// <summary>Сессия открыта, сообщения приходят.</summary>
    bool IsConnected { get; }

    /// <summary>Причина последнего обрыва или отказа. Пусто — проблем нет.</summary>
    string LastError { get; }

    /// <summary>Новое сообщение из чата. Прилетает из потока чтения, а не из UI.</summary>
    event Action<TwitchChatMessage>? MessageReceived;

    /// <summary>Соединение поднялось или упало.</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>Начать читать чат канала от имени аккаунта.</summary>
    bool Start(string broadcasterUserId, string readerUserId);

    /// <summary>Перестать читать. Безопасно звать, когда уже остановлено.</summary>
    void Stop();
}