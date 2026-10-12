using SoundMeeter.Core.Contract;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Чтение и запись параметров стрипов по ключу привязки.
///
/// Это единственное место, где ключ из <see cref="MidiParameters"/> превращается
/// в обращение к стрипу. Держим его отдельно по трём причинам.
///
/// Проверяемость. Раньше эти ~140 строк жили приватными статическими методами
/// ViewModel, и тесты доставали их рефлексией по имени: переименование метода
/// ломало тесты с сообщением «метод не найден» вместо внятной ошибки. Здесь
/// можно вызвать напрямую и подставить свой стрип.
///
/// Закрытый набор. Какие параметры бывают у входа и у шины — известно только
/// здесь. Список рассыпан по ViewModel, где он смешан с логикой кнопок и
/// разбора сообщений, и поэтому не проверяется ничем: параметр, добавленный в
/// <see cref="MidiParameters"/> и забытый в этом switch, «молча не работает».
///
/// Ошибки видны. Раньше отсутствующий стрип или неподдерживаемый параметр
/// просто игнорировались, и у пользователя было «кнопка не реагирует» без
/// объяснения. Теперь это <see cref="MidiError.StripMissing"/> и
/// <see cref="MidiError.ParameterUnsupported"/> — с текстом в журнале.
/// </summary>
public sealed class MidiStripAccessor
{
    private readonly IStripProvider _strips;

    public MidiStripAccessor(IStripProvider strips) => _strips = strips;

    /// <summary>
    /// Значение параметра. <see cref="MidiError.StripMissing"/>, если стрипа
    /// нет, и <see cref="MidiError.ParameterUnsupported"/>, если параметра на
    /// нём нет — второе обычно означает привязку кнопки эффекта к шине.
    /// </summary>
    public MidiResult<bool> GetButton(string targetType, string stripId, string parameter)
    {
        switch (targetType)
        {
            case MidiTargetTypes.Input:
                var input = FindInput(stripId);
                if (input is null) return MidiResult<bool>.Fail(MidiError.StripMissing);
                return TryReadButton(input, parameter, out var value)
                    ? MidiResult<bool>.Ok(value)
                    : MidiResult<bool>.Fail(MidiError.ParameterUnsupported);

            case MidiTargetTypes.Bus:
                var bus = FindBus(stripId);
                if (bus is null) return MidiResult<bool>.Fail(MidiError.StripMissing);
                return TryReadButton(bus, parameter, out var busValue)
                    ? MidiResult<bool>.Ok(busValue)
                    : MidiResult<bool>.Fail(MidiError.ParameterUnsupported);

            default:
                return MidiResult<bool>.Fail(MidiError.ParameterUnsupported);
        }
    }

    public MidiResult SetButton(string targetType, string stripId, string parameter, bool value)
    {
        switch (targetType)
        {
            case MidiTargetTypes.Input:
                var input = FindInput(stripId);
                if (input is null) return MidiResult.Fail(MidiError.StripMissing);
                if (!TryWriteButton(input, parameter, value))
                    return MidiResult.Fail(MidiError.ParameterUnsupported);
                return MidiResult.Ok;

            case MidiTargetTypes.Bus:
                var bus = FindBus(stripId);
                if (bus is null) return MidiResult.Fail(MidiError.StripMissing);
                if (!TryWriteButton(bus, parameter, value))
                    return MidiResult.Fail(MidiError.ParameterUnsupported);
                return MidiResult.Ok;

            default:
                return MidiResult.Fail(MidiError.ParameterUnsupported);
        }
    }

    public MidiResult ToggleButton(string targetType, string stripId, string parameter)
    {
        var current = GetButton(targetType, stripId, parameter);
        if (!current.IsSuccess) return MidiResult.Fail(current.Error);
        return SetButton(targetType, stripId, parameter, !current.Value);
    }

    /// <summary>Запись непрерывного параметра (фейдер, крутилка).</summary>
    public MidiResult SetContinuous(string targetType, string stripId, string parameter, float value)
    {
        switch (targetType)
        {
            case MidiTargetTypes.Input:
                var input = FindInput(stripId);
                if (input is null) return MidiResult.Fail(MidiError.StripMissing);
                return TryWriteContinuous(input, parameter, value)
                    ? MidiResult.Ok
                    : MidiResult.Fail(MidiError.ParameterUnsupported);

            case MidiTargetTypes.Bus:
                var bus = FindBus(stripId);
                if (bus is null) return MidiResult.Fail(MidiError.StripMissing);
                return TryWriteContinuous(bus, parameter, value)
                    ? MidiResult.Ok
                    : MidiResult.Fail(MidiError.ParameterUnsupported);

            default:
                return MidiResult.Fail(MidiError.ParameterUnsupported);
        }
    }

    /// <summary>
    /// Параметр ведёт себя как кнопка: у него есть только два состояния.
    /// Отдельная проверка, потому что режимы Hold и Latch применяются только к
    /// кнопкам, и применение их к фейдеру значило бы «запомнить и не отпускать».
    /// </summary>
    public static bool IsButtonParameter(string parameter) => parameter switch
    {
        "IsMuted" or "IsSolo" or "IsMono" or "DenoiserEnabled" => true,
        MidiParameters.Func1Key or MidiParameters.Func2Key => true,
        MidiParameters.CompressorEnabledKey or MidiParameters.FxGainEnabledKey
            or MidiParameters.DelayEnabledKey or MidiParameters.ReverbEnabledKey
            or MidiParameters.EqEnabledKey => true,
        _ => false
    };

    /// <summary>
    /// Состояние параметра, пока кнопка нажата. У Mute это «звук включён» —
    /// то есть параметр снимается, а не ставится. Поэтому здесь сравнение, а
    /// не просто <c>true</c>: иначе PTT на mute глушил бы микрофон вместо
    /// открытия.
    /// </summary>
    public static bool ButtonActiveValue(string parameter) => parameter != "IsMuted";

    private IInputStrip? FindInput(string stripId) =>
        _strips.Inputs.FirstOrDefault(i => i.Id == stripId);

    private IOutputStrip? FindBus(string stripId) =>
        _strips.Buses.FirstOrDefault(b => b.Id == stripId);

    private static bool TryReadButton(IInputStrip strip, string parameter, out bool value)
    {
        switch (parameter)
        {
            case "IsMuted": value = strip.IsMuted; return true;
            case "IsSolo": value = strip.IsSolo; return true;
            case "IsMono": value = strip.IsMono; return true;
            case "DenoiserEnabled": value = strip.DenoiserEnabled; return true;

            case MidiParameters.Func1Key: value = strip.Func1Engaged; return true;
            case MidiParameters.Func2Key: value = strip.Func2Engaged; return true;

            case MidiParameters.CompressorEnabledKey: value = strip.CompressorEnabled; return true;
            case MidiParameters.FxGainEnabledKey: value = strip.FxGainEnabled; return true;
            case MidiParameters.DelayEnabledKey: value = strip.DelayEnabled; return true;
            case MidiParameters.ReverbEnabledKey: value = strip.ReverbEnabled; return true;
            case MidiParameters.EqEnabledKey: value = strip.EqEnabled; return true;

            default: value = false; return false;
        }
    }

    private static bool TryReadButton(IOutputStrip strip, string parameter, out bool value)
    {
        switch (parameter)
        {
            case "IsMuted": value = strip.IsMuted; return true;
            case "IsSolo": value = strip.IsSolo; return true;
            case "IsMono": value = strip.IsMono; return true;

            default: value = false; return false;
        }
    }

    private static bool TryWriteButton(IInputStrip strip, string parameter, bool value)
    {
        switch (parameter)
        {
            case "IsMuted": strip.IsMuted = value; return true;
            case "IsSolo": strip.IsSolo = value; return true;
            case "IsMono": strip.IsMono = value; return true;
            case "DenoiserEnabled": strip.DenoiserEnabled = value; return true;

            case MidiParameters.Func1Key: strip.Func1Engaged = value; return true;
            case MidiParameters.Func2Key: strip.Func2Engaged = value; return true;

            case MidiParameters.CompressorEnabledKey: strip.CompressorEnabled = value; return true;
            case MidiParameters.FxGainEnabledKey: strip.FxGainEnabled = value; return true;
            case MidiParameters.DelayEnabledKey: strip.DelayEnabled = value; return true;
            case MidiParameters.ReverbEnabledKey: strip.ReverbEnabled = value; return true;
            case MidiParameters.EqEnabledKey: strip.EqEnabled = value; return true;

            default: return false;
        }
    }

    private static bool TryWriteButton(IOutputStrip strip, string parameter, bool value)
    {
        switch (parameter)
        {
            case "IsMuted": strip.IsMuted = value; return true;
            case "IsSolo": strip.IsSolo = value; return true;
            case "IsMono": strip.IsMono = value; return true;

            default: return false;
        }
    }

    private static bool TryWriteContinuous(IInputStrip strip, string parameter, float value)
    {
        switch (parameter)
        {
            case "VolumeDb": strip.VolumeDb = value; return true;
            case "GainDb": strip.GainDb = value; return true;
            case "DenoiserNoiseRemover": strip.DenoiserNoiseRemover = value; return true;
            case "DenoiserDryWet": strip.DenoiserDryWet = value; return true;
            case "DenoiserFormantLowDb": strip.DenoiserFormantLowDb = value; return true;
            case "DenoiserFormantMidDb": strip.DenoiserFormantMidDb = value; return true;
            case "DenoiserFormantHighDb": strip.DenoiserFormantHighDb = value; return true;
            case "DenoiserFormantGroupDb": strip.DenoiserFormantGroupDb = value; return true;

            default: return false;
        }
    }

    private static bool TryWriteContinuous(IOutputStrip strip, string parameter, float value)
    {
        if (parameter != "VolumeDb") return false;
        strip.VolumeDb = value;
        return true;
    }
}