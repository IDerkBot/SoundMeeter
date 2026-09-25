namespace SoundMeeter.Models;

/// <summary>Тип MIDI-сообщения, к которому привязан параметр.</summary>
public enum MidiMessageKind
{
    ControlChange = 1,
    NoteOn = 2,
    PitchWheel = 3,

    /// <summary>Note Off (или NoteOn с velocity=0) — отпускание ноты.</summary>
    NoteOff = 4
}

/// <summary>Форма управления параметром.</summary>
public enum MidiParamShape
{
    /// <summary>Непрерывный фейдер — CC/PitchWheel маппится на диапазон Min..Max.</summary>
    Fader,

    /// <summary>Непрерывная крутилка — CC маппится на диапазон Min..Max.</summary>
    Knob,

    /// <summary>Кнопка — переключение (toggle) по нажатию.</summary>
    Button
}

/// <summary>Описание параметра стрипа, доступного для MIDI-привязки.</summary>
public sealed class MidiParameterDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool ForInput { get; init; }
    public bool ForBus { get; init; }
    public MidiParamShape Shape { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
}

/// <summary>Каталог параметров, доступных для MIDI-привязки.</summary>
public static class MidiParameters
{
    public static readonly IReadOnlyList<MidiParameterDescriptor> All = new List<MidiParameterDescriptor>
    {
        new() { Key = "VolumeDb", Label = "Volume", ForInput = true, ForBus = true, Shape = MidiParamShape.Fader, Min = -60, Max = 12 },
        new() { Key = "IsMuted", Label = "Mute", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "IsMono", Label = "Mono", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "IsSolo", Label = "Solo", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "DenoiserEnabled", Label = "Denoiser", ForInput = true, Shape = MidiParamShape.Button },
        new() { Key = "DenoiserNoiseRemover", Label = "DEN Noise", ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
        new() { Key = "DenoiserDryWet", Label = "DEN Dry/Wet", ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
        new() { Key = "DenoiserFormantLowDb", Label = "DEN Form. Low", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantMidDb", Label = "DEN Form. Mid", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantHighDb", Label = "DEN Form. High", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantGroupDb", Label = "DEN Form. Gain", ForInput = true, Shape = MidiParamShape.Knob, Min = -12, Max = 12 }
    };

    public static IReadOnlyList<MidiParameterDescriptor> ForInput() =>
        All.Where(d => d.ForInput).ToList();

    public static IReadOnlyList<MidiParameterDescriptor> ForBus() =>
        All.Where(d => d.ForBus).ToList();
}

/// <summary>
/// Привязка MIDI-сообщения к параметру стрипа.
/// TargetType: "Input" или "Bus"; StripId — стабильный Id стрипа.
/// </summary>
public class MidiBinding
{
    public string TargetType { get; set; } = "Input";
    public string StripId { get; set; } = "";
    public string Parameter { get; set; } = "VolumeDb";
    public int Channel { get; set; }
    public int Control { get; set; }
    public MidiMessageKind MessageKind { get; set; } = MidiMessageKind.ControlChange;

    /// <summary>true — кнопка работает как PTT: пока нажата — активно, на отпускание — возврат.</summary>
    public bool IsMomentary { get; set; }
}

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
            IsMomentary = b.IsMomentary
        }).ToList()
    };
}