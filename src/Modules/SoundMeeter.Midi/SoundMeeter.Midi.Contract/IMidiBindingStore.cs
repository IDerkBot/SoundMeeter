using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Contract;

public interface IMidiBindingStore
{
    IReadOnlyList<MidiBinding> All { get; }
    string? DeviceName { get; set; }
    MidiBinding? Find(string targetType, string stripId, string parameter);
    void Replace(MidiBinding binding);
    void Remove(string targetType, string stripId, string parameter);
}
