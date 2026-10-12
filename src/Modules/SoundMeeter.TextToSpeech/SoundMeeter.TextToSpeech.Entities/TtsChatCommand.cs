
namespace SoundMeeter.TextToSpeech.Entities;

/// <summary>
/// Разобранная команда чата. Неизвестные команды и отказы возвращаются тоже, а не
/// как null: по разбору видно, что именно произошло, и это попадает в журнал.
/// Молча проглоченная команда выглядела бы как «модуль не работает».
/// </summary>
public sealed class TtsChatCommand
{
    private TtsChatCommand(TtsChatAction action, string user, string text, string? voice,
        TtsChatReject reject, bool truncated)
    {
        Action = action;
        User = user;
        Text = text;
        Voice = voice;
        Reason = reject;
        Truncated = truncated;
    }

    /// <summary>Что делать.</summary>
    public TtsChatAction Action { get; }

    /// <summary>Логин автора, как пришёл (нормализован: без «@», обрезан по длине).</summary>
    public string User { get; }

    /// <summary>Текст для озвучки: очищенный и обрезанный по <c>MaxMessageChars</c>.</summary>
    public string Text { get; }

    /// <summary>Имя голоса для <see cref="TtsChatAction.SetVoice"/>.</summary>
    public string? Voice { get; }

    /// <summary>Причина отказа; <see cref="TtsChatReject.None"/> — команда принята.</summary>
    public TtsChatReject Reason { get; }

    /// <summary>Текст реплики длиннее предела и был обрезан.</summary>
    public bool Truncated { get; }

    /// <summary>Принять ли команду в работу.</summary>
    public bool IsAccepted => Action != TtsChatAction.None && Reason == TtsChatReject.None;

    // Фабрики публичные не из красоты: часть отказов знает только модуль, а не
    // разборщик сообщения — выключен он, недоступен синтез или сработал дроссель.
    // Разборщик живёт в этой же сборке, а сервис — в следующей, и без public он
    // не смог бы сообщить о них тем же типом, что и обо всём остальном.

    /// <summary>Реплика принята в озвучку.</summary>
    public static TtsChatCommand Speak(string user, string text, bool truncated) =>
        new(TtsChatAction.Speak, user, text, null, TtsChatReject.None, truncated);

    /// <summary>Автору назначен голос.</summary>
    public static TtsChatCommand SetVoice(string user, string voice) =>
        new(TtsChatAction.SetVoice, user, "", voice, TtsChatReject.None, false);

    /// <summary>Реплика не озвучена: <paramref name="reason"/> объясняет почему.</summary>
    public static TtsChatCommand Rejected(string user, TtsChatReject reason) =>
        new(TtsChatAction.None, user, "", null, reason, false);
}
