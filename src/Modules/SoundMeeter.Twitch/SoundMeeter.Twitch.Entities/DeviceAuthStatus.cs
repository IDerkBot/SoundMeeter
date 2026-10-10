namespace SoundMeeter.Twitch.Entities;

/// <summary>Что ответил сервер на попытку забрать токен.</summary>
public enum DeviceAuthStatus
{
    /// <summary>Пользователь ещё не подтвердил. Ждём и спрашиваем снова.</summary>
    Pending,

    /// <summary>Токен получен и сохранён.</summary>
    Authorized,

    /// <summary>Пользователь отказал.</summary>
    Denied,

    /// <summary>Код истёк или был уже использован.</summary>
    Expired,

    /// <summary>Что-то пошло не так: сеть, ответ без токена, нет user_id.</summary>
    Failed,
}
