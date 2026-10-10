namespace SoundMeeter.Twitch.Entities;

/// <summary>Разобранное сообщение протокола: что это за кадр.</summary>
public enum EventSubFrame
{
    /// <summary>Неизвестный кадр или мусор. Игнорируется.</summary>
    Unknown,

    /// <summary>Сессия открыта, сервер ждёт подписку. В кадре — идентификатор сессии.</summary>
    Welcome,

    /// <summary>Соединение живо. Ничего не делает.</summary>
    Keepalive,

    /// <summary>
    /// Сервер просит переподключиться на другой адрес и закрывает старое соединение.
    /// В кадре — новый адрес.
    /// </summary>
    Reconnect,

    /// <summary>Подписка отозвана: аккаунт удалён, доступ отозван или версия события больше не поддерживается.</summary>
    Revocation,

    /// <summary>Сообщение из чата.</summary>
    ChatMessage,
}
