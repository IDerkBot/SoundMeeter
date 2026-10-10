namespace SoundMeeter.Twitch.Entities;

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
