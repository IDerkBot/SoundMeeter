using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TextToSpeech.Entities;

/// <summary>Что команда из чата должна сделать.</summary>
public enum TtsChatAction
{
    /// <summary>Не команда TTS: обычное сообщение, модуль его не читает.</summary>
    None,

    /// <summary>Озвучить текст реплики.</summary>
    Speak,

    /// <summary>Запомнить голос, выбранный пользователем.</summary>
    SetVoice,
}
