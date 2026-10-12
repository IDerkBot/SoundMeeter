using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TextToSpeech.Entities;

/// <summary>Почему команда не была выполнена (<see cref="TtsChatAction.None"/> — не ошибка).</summary>
public enum TtsChatReject
{
    /// <summary>Команда принята.</summary>
    None,

    /// <summary>Сообщение не начинается ни с одной из команд модуля.</summary>
    NotACommand,

    /// <summary>В игнорируемом списке.</summary>
    IgnoredUser,

    /// <summary>Команда есть, а сказать нечего: пустой текст или одни смайлы.</summary>
    EmptyText,

    /// <summary>Смена голоса выключена в настройках.</summary>
    VoiceChangeDisabled,

    /// <summary>Голоса с таким именем нет в системе.</summary>
    UnknownVoice,

    /// <summary>
    /// Реплик от пользователя больше, чем разрешено настройками. Разбор команд о
    /// ней не знает: решение принимает модуль, у которого есть окно времени.
    /// </summary>
    RateLimited,

    /// <summary>Модуль выключен — озвучивать нечего.</summary>
    ModuleOff,

    /// <summary>В системе нет синтеза речи.</summary>
    EngineUnavailable,
}
