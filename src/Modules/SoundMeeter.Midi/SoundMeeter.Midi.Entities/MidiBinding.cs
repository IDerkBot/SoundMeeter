using System.Text.Json.Serialization;

namespace SoundMeeter.Midi.Entities;

/// <summary>
/// Привязка MIDI-сообщения к параметру стрипа.
/// TargetType: "Input" или "Bus"; StripId — стабильный Id стрипа.
/// </summary>
public class MidiBinding
{
    public string TargetType { get; set; } = MidiTargetTypes.Input;
    public string StripId { get; set; } = "";
    public string Parameter { get; set; } = "VolumeDb";
    public int Channel { get; set; }
    public int Control { get; set; }
    public MidiMessageKind MessageKind { get; set; } = MidiMessageKind.ControlChange;

    /// <summary>Режим кнопки: переключение, удержание (PTT) или кнопка с фиксацией.</summary>
    public MidiButtonMode Mode { get; set; } = MidiButtonMode.Toggle;

    /// <summary>
    /// true — обратная полярность CC: 0 = нажато, 127 = отпущено.
    /// Так шлют, например, кнопки с фиксацией на некоторых MIDI-микшерах.
    /// </summary>
    public bool IsInverted { get; set; }

    /// <summary>
    /// Устаревшее поле из настроек прошлых версий (hold-режим).
    /// Читается при загрузке и переносится в <see cref="Mode"/>; не сохраняется.
    /// </summary>
    [JsonPropertyName("IsMomentary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyMomentary
    {
        get => null;
        set { if (value == true) Mode = MidiButtonMode.Hold; }
    }
}
