namespace SoundMeeter.Midi.Entities
{
    /// <summary>Тип MIDI-сообщения, к которому привязан параметр.</summary>
    public enum MidiMessageKind
    {
        ControlChange = 1,
        NoteOn = 2,
        PitchWheel = 3,

        /// <summary>Note Off (или NoteOn с velocity=0) — отпускание ноты.</summary>
        NoteOff = 4
    }
}
