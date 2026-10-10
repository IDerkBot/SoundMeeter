using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Contract;

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
