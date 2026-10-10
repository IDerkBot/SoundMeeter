using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Contract;

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
