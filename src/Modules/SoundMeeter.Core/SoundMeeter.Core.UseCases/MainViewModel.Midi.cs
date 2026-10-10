using SoundMeeter.Midi.UseCases;

namespace SoundMeeter.Core.UseCases;

// MIDI-привязки вынесены в модуль SoundMeeter.Midi (сценарии в .UseCases).
// Этот partial оставлен как фасад: ядро знает, что MIDI-модуль есть и как его
// открыть при старте, но не знает, как устроены привязки. Всё, что нужно было
// раньше наружу (MidiSettingsChanged, ResetMidiButtonStates), оказалось чистыми
// форвардами без единого вызова — сценарии вызываются напрямую тем, кто меняет
// настройки, то есть окном привязок.
public partial class MainViewModel
{
    /// <summary>Модуль MIDI (для окна привязок).</summary>
    public MidiController Midi => _midi;
}