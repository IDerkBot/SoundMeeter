namespace SoundMeeter.Midi.Entities
{
    /// <summary>Настройки MIDI-микшера: устройство ввода и список привязок.</summary>
    public class MidiSettings
    {
        /// <summary>ProductName MIDI-устройства ввода (устойчиво к смене индексов).</summary>
        public string? DeviceName { get; set; }

        public List<MidiBinding> Bindings { get; set; } = new();

        public MidiSettings Clone() => new()
        {
            DeviceName = DeviceName,
            Bindings = Bindings.Select(b => new MidiBinding
            {
                TargetType = b.TargetType,
                StripId = b.StripId,
                Parameter = b.Parameter,
                Channel = b.Channel,
                Control = b.Control,
                MessageKind = b.MessageKind,
                Mode = b.Mode,
                IsInverted = b.IsInverted
            }).ToList()
        };
    }
}
