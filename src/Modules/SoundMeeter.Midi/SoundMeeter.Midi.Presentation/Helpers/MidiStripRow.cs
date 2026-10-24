using System.Collections.ObjectModel;

namespace SoundMeeter.Midi.Presentation.Helpers;

/// <summary>
/// Строка стрипа в MIDI-окне: заголовок + список групп параметров.
/// Группы параметров сворачиваются в экспандеры.
/// </summary>
public class MidiStripRow
{
    public required string Title { get; init; }
    public required string TargetType { get; init; }
    public required string StripId { get; init; }
    public ObservableCollection<MidiParamGroupRow> Groups { get; } = new();
}
