using SoundMeeter.Midi.UseCases;


namespace SoundMeeter.Core.Presentation.ViewModels;

// MIDI-привязки вынесены в модуль SoundMeeter.Midi (сценарии в .UseCases).
// Этот partial оставлен как фасад: окно привязок берёт модуль у главной VM.
// Всё, что нужно было раньше наружу (MidiSettingsChanged, ResetMidiButtonStates),
// оказалось чистыми форвардами без единого вызова — сценарии вызываются
// напрямую тем, кто меняет настройки, то есть окном привязок.
public partial class MainViewModel
{
    /// <summary>Модуль MIDI (для окна привязок).</summary>
    public MidiController Midi => _coordinator.Midi;
}