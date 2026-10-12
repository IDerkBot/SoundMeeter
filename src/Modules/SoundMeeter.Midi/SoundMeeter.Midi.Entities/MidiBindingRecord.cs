using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Midi.Entities;

/// <summary>
/// Application DTO: сырые данные о MIDI-привязке.
/// Не содержит методов, не знает об UI. Immutable.
/// </summary>
public sealed record MidiBindingRecord(
    string TargetType,    // "Input" / "Bus"
    string StripId,       // ID стрипа
    string ParameterKey,  // Ключ параметра (например, "Volume", "Mute")
    int Channel,          // MIDI-канал (0-15)
    int Control,          // Номер CC / Note
    MidiMessageKind Kind, // Тип сообщения (CC, NoteOn, ...)
    MidiButtonMode Mode,  // Режим кнопки (Toggle, PTT, Latch)
    bool IsInverted       // Инверсия полярности
);
