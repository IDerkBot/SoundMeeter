using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

public class GetMidiBindingQuery
{
    private readonly IMidiBindingStore _store;

    public string DeviceName => _store.DeviceName;

    public GetMidiBindingQuery(IMidiBindingStore store) { _store = store; }

    // Возвращает DTO приложения, а не представления!
    public MidiBindingRecord? Execute(string targetType, string stripId, string parameter)
    {
        var domainBinding = _store.Find(targetType, stripId, parameter);
        return domainBinding == null ? null : new MidiBindingRecord("", "", "", 0, 0, MidiMessageKind.NoteOff, MidiButtonMode.Toggle, false);
    }
}
