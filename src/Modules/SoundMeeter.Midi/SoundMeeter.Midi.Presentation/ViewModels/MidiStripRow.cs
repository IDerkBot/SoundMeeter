using System.Collections.ObjectModel;

namespace SoundMeeter.Midi.Presentation.ViewModels;

/// <summary>
/// Строка стрипа в MIDI-окне: заголовок + список ячеек параметров.
/// </summary>
public class MidiStripRow
{
    public required string Title { get; init; }
    public required string TargetType { get; init; }
    public required string StripId { get; init; }
    public ObservableCollection<MidiParamCell> Params { get; } = new();
}
