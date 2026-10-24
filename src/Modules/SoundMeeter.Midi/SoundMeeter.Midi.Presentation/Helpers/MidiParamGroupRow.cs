using SoundMeeter.Midi.Presentation.ViewModels;
using System.Collections.ObjectModel;

namespace SoundMeeter.Midi.Presentation.Helpers;

/// <summary>
/// Группа параметров в MIDI-окне: заголовок + список ячеек.
/// Используется для сворачивания связанных параметров в экспандеры.
/// </summary>
public class MidiParamGroupRow
{
    public required string Title { get; set; }
    public required string GroupKey { get; init; }
    public ObservableCollection<MidiParamCell> Params { get; } = new();
}
