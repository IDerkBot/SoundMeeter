using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Contract;

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
