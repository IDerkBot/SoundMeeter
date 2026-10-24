namespace SoundMeeter.Midi.Entities;

/// <summary>Каталог параметров, доступных для MIDI-привязки.</summary>
public static class MidiParameters
{
    //  Ключи групп: используются UI для сворачивания параметров в экспандеры.
    public const string GroupBasic = "Sm.Midi.Group.Basic";
    public const string GroupFunc = "Sm.Midi.Group.Func";
    public const string GroupFx = "Sm.Midi.Group.Fx";
    public const string GroupDenoiser = "Sm.Midi.Group.Denoiser";

    /// <summary>Ключ привязки кнопки FUNC1 (слот 1 входного стрипа).</summary>
    public const string Func1Key = "Func1";

    /// <summary>Ключ привязки кнопки FUNC2 (слот 2 входного стрипа).</summary>
    public const string Func2Key = "Func2";

    /// <summary>Ключ привязки включения компрессора входного стрипа.</summary>
    public const string CompressorEnabledKey = "CompressorEnabled";

    /// <summary>Ключ привязки включения trim после компрессора.</summary>
    public const string FxGainEnabledKey = "FxGainEnabled";

    /// <summary>Ключ привязки включения задержки.</summary>
    public const string DelayEnabledKey = "DelayEnabled";

    /// <summary>Ключ привязки включения реверберации.</summary>
    public const string ReverbEnabledKey = "ReverbEnabled";

    /// <summary>Ключ привязки включения эквалайзера.</summary>
    public const string EqEnabledKey = "EqEnabled";

    public static readonly IReadOnlyList<MidiParameterDescriptor> All = new List<MidiParameterDescriptor>
 {
    new() { Key = "VolumeDb", LabelKey = "Sm.Midi.Param.VolumeDb", GroupKey = GroupBasic, ForInput = true, ForBus = true, Shape = MidiParamShape.Fader, Min = -60, Max = 12 },
    new() { Key = "GainDb", LabelKey = "Sm.Midi.Param.GainDb", GroupKey = GroupBasic, ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 60 },
    new() { Key = "IsMuted", LabelKey = "Sm.Midi.Param.IsMuted", GroupKey = GroupBasic, ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
    new() { Key = "IsMono", LabelKey = "Sm.Midi.Param.IsMono", GroupKey = GroupBasic, ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
    new() { Key = "IsSolo", LabelKey = "Sm.Midi.Param.IsSolo", GroupKey = GroupBasic, ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
    new() { Key = "DenoiserEnabled", LabelKey = "Sm.Midi.Param.DenoiserEnabled", GroupKey = GroupBasic, ForInput = true, Shape = MidiParamShape.Button },

    //  Пользовательские кнопки FUNC стрипа. Ключи — по одному на слот
    //  (Func1/Func2); число слотов задаёт InputChannelModel.FuncButtonSlotCount,
    //  поэтому список и разметка ленты не должны с ними разойтись.
    new() { Key = Func1Key, LabelKey = "Sm.Midi.Param.Func1", GroupKey = GroupFunc, ForInput = true, Shape = MidiParamShape.Button },
    new() { Key = Func2Key, LabelKey = "Sm.Midi.Param.Func2", GroupKey = GroupFunc, ForInput = true, Shape = MidiParamShape.Button },

    //  Включение эффектов стрипа (SM-B05). Сами крутилки эффектов в MIDI
    //  не выведены: это 13 строк на каждый вход в окне привязок, и ими
    //  пользуются мышью; по MIDI разумно переключать эффект целиком.
    new() { Key = CompressorEnabledKey, LabelKey = "Sm.Midi.Param.Compressor", GroupKey = GroupFx, ForInput = true, Shape = MidiParamShape.Button },
    new() { Key = FxGainEnabledKey, LabelKey = "Sm.Midi.Param.FxGain", GroupKey = GroupFx, ForInput = true, Shape = MidiParamShape.Button },
    new() { Key = DelayEnabledKey, LabelKey = "Sm.Midi.Param.Delay", GroupKey = GroupFx, ForInput = true, Shape = MidiParamShape.Button },
    new() { Key = ReverbEnabledKey, LabelKey = "Sm.Midi.Param.Reverb", GroupKey = GroupFx, ForInput = true, Shape = MidiParamShape.Button },
    new() { Key = EqEnabledKey, LabelKey = "Sm.Midi.Param.Eq", GroupKey = GroupFx, ForInput = true, Shape = MidiParamShape.Button },

    new() { Key = "DenoiserNoiseRemover", LabelKey = "Sm.Midi.Param.DenoiserNoiseRemover", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
    new() { Key = "DenoiserDryWet", LabelKey = "Sm.Midi.Param.DenoiserDryWet", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
    new() { Key = "DenoiserFormantLowDb", LabelKey = "Sm.Midi.Param.DenoiserFormantLowDb", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
    new() { Key = "DenoiserFormantMidDb", LabelKey = "Sm.Midi.Param.DenoiserFormantMidDb", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
    new() { Key = "DenoiserFormantHighDb", LabelKey = "Sm.Midi.Param.DenoiserFormantHighDb", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
    new() { Key = "DenoiserFormantGroupDb", LabelKey = "Sm.Midi.Param.DenoiserFormantGroupDb", GroupKey = GroupDenoiser, ForInput = true, Shape = MidiParamShape.Knob, Min = -12, Max = 12 }
 };

    public static IReadOnlyList<MidiParameterDescriptor> ForInput() =>
        All.Where(d => d.ForInput).ToList();

    public static IReadOnlyList<MidiParameterDescriptor> ForBus() =>
        All.Where(d => d.ForBus).ToList();
}
