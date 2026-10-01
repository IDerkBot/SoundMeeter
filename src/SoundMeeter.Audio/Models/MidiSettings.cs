using System.Text.Json.Serialization;

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

    /// <summary>Ключ подписи в <c>Resources/Strings.resx</c> (SM-C07).</summary>
    public string LabelKey { get; init; } = "";

    /// <summary>
    /// Подпись параметра на текущем языке. Считается, а не хранится: смена языка
    /// не должна требовать пересоздания дескрипторов (они статические).
    /// </summary>
    public string Label => Services.Loc.Get(LabelKey);

    public bool ForInput { get; init; }
    public bool ForBus { get; init; }
    public MidiParamShape Shape { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
}

/// <summary>Каталог параметров, доступных для MIDI-привязки.</summary>
public static class MidiParameters
{
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

    public static readonly IReadOnlyList<MidiParameterDescriptor> All = new List<MidiParameterDescriptor>
    {
        new() { Key = "VolumeDb", LabelKey = "Sm.Midi.Param.VolumeDb", ForInput = true, ForBus = true, Shape = MidiParamShape.Fader, Min = -60, Max = 12 },
        new() { Key = "GainDb", LabelKey = "Sm.Midi.Param.GainDb", ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 60 },
        new() { Key = "IsMuted", LabelKey = "Sm.Midi.Param.IsMuted", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "IsMono", LabelKey = "Sm.Midi.Param.IsMono", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "IsSolo", LabelKey = "Sm.Midi.Param.IsSolo", ForInput = true, ForBus = true, Shape = MidiParamShape.Button },
        new() { Key = "DenoiserEnabled", LabelKey = "Sm.Midi.Param.DenoiserEnabled", ForInput = true, Shape = MidiParamShape.Button },

        //  Пользовательские кнопки FUNC стрипа. Ключи — по одному на слот
        //  (Func1/Func2); число слотов задаёт InputChannelModel.FuncButtonSlotCount,
        //  поэтому список и разметка ленты не должны с ними разойтись.
        new() { Key = Func1Key, LabelKey = "Sm.Midi.Param.Func1", ForInput = true, Shape = MidiParamShape.Button },
        new() { Key = Func2Key, LabelKey = "Sm.Midi.Param.Func2", ForInput = true, Shape = MidiParamShape.Button },

        //  Включение эффектов стрипа (SM-B05). Сами крутилки эффектов в MIDI
        //  не выведены: это 13 строк на каждый вход в окне привязок, и ими
        //  пользуются мышью; по MIDI разумно переключать эффект целиком.
        new() { Key = CompressorEnabledKey, LabelKey = "Sm.Midi.Param.Compressor", ForInput = true, Shape = MidiParamShape.Button },
        new() { Key = FxGainEnabledKey, LabelKey = "Sm.Midi.Param.FxGain", ForInput = true, Shape = MidiParamShape.Button },
        new() { Key = DelayEnabledKey, LabelKey = "Sm.Midi.Param.Delay", ForInput = true, Shape = MidiParamShape.Button },
        new() { Key = ReverbEnabledKey, LabelKey = "Sm.Midi.Param.Reverb", ForInput = true, Shape = MidiParamShape.Button },

        new() { Key = "DenoiserNoiseRemover", LabelKey = "Sm.Midi.Param.DenoiserNoiseRemover", ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
        new() { Key = "DenoiserDryWet", LabelKey = "Sm.Midi.Param.DenoiserDryWet", ForInput = true, Shape = MidiParamShape.Knob, Min = 0, Max = 100 },
        new() { Key = "DenoiserFormantLowDb", LabelKey = "Sm.Midi.Param.DenoiserFormantLowDb", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantMidDb", LabelKey = "Sm.Midi.Param.DenoiserFormantMidDb", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantHighDb", LabelKey = "Sm.Midi.Param.DenoiserFormantHighDb", ForInput = true, Shape = MidiParamShape.Knob, Min = -24, Max = 24 },
        new() { Key = "DenoiserFormantGroupDb", LabelKey = "Sm.Midi.Param.DenoiserFormantGroupDb", ForInput = true, Shape = MidiParamShape.Knob, Min = -12, Max = 12 }
    };

    public static IReadOnlyList<MidiParameterDescriptor> ForInput() =>
        All.Where(d => d.ForInput).ToList();

    public static IReadOnlyList<MidiParameterDescriptor> ForBus() =>
        All.Where(d => d.ForBus).ToList();
}

/// <summary>Режим обработки кнопочной MIDI-привязки.</summary>
public enum MidiButtonMode
{
    /// <summary>Обычная кнопка: переключение параметра по фронту нажатия.</summary>
    Toggle = 0,

    /// <summary>PTT по удержанию: пока нажато — параметр активен, на отпускание — возврат.</summary>
    Hold = 1,

    /// <summary>
    /// Кнопка с фиксацией (latching): контроллер шлёт не нажатие, а состояние
    /// «включено/выключено», поэтому параметр ставится ровно в это состояние.
    /// </summary>
    Latch = 2
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

    /// <summary>Режим кнопки: переключение, удержание (PTT) или кнопка с фиксацией.</summary>
    public MidiButtonMode Mode { get; set; } = MidiButtonMode.Toggle;

    /// <summary>
    /// true — обратная полярность CC: 0 = нажато, 127 = отпущено.
    /// Так шлют, например, кнопки с фиксацией на некоторых MIDI-микшерах.
    /// </summary>
    public bool IsInverted { get; set; }

    /// <summary>
    /// Устаревшее поле из настроек прошлых версий (hold-режим).
    /// Читается при загрузке и переносится в <see cref="Mode"/>; не сохраняется.
    /// </summary>
    [JsonPropertyName("IsMomentary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyMomentary
    {
        get => null;
        set { if (value == true) Mode = MidiButtonMode.Hold; }
    }
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
            Mode = b.Mode,
            IsInverted = b.IsInverted
        }).ToList()
    };
}