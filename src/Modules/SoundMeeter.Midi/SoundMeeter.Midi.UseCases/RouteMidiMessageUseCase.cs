using Microsoft.Extensions.Logging;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Применение MIDI-сообщения к привязкам стрипов.
///
/// Самый горячий сценарий модуля: приходит на каждое движение контроллера, то
/// есть десятки раз в секунду. Отсюда два требования, которые и определяют его
/// форму. Ничего не спрашивать у вызывающего — он на MIDI-потоке, и синхронный
/// опрос настроек здесь означал бы гонку с пресетом. И не бросать исключений:
/// единственный способ «уронить» приложение с MIDI-контроллера — это прислать
/// с него не то сообщение.
///
/// Привязки читаются заново на каждое сообщение. Это осознанно: список лежит в
/// настройках, которые пересобираются при импорте пресета, и кэш здесь разошёлся
/// бы с настройками тихо — привязки перестали бы применяться, а прибор молчал.
///
/// Вызывается только на UI-потоке: маршалинг на диспетчере — дело
/// <see cref="MidiController"/>, который один на весь модуль и не даёт
/// подписчикам разъезжаться по потокам.
/// </summary>
public sealed class RouteMidiMessageUseCase
{
    private readonly IMidiBindingStore _bindings;
    private readonly MidiStripAccessor _strips;
    private readonly MidiPressTracker _pressed;
    private readonly ILogger _logger = AppLog.For<RouteMidiMessageUseCase>();

    public RouteMidiMessageUseCase(
        IMidiBindingStore bindings,
        MidiStripAccessor strips,
        MidiPressTracker pressed)
    {
        _bindings = bindings;
        _strips = strips;
        _pressed = pressed;
    }

    /// <summary>
    /// Применяет сообщение ко всем подходящим привязкам.
    ///
    /// Возвращает <see cref="MidiError.StripMissing"/> или
    /// <see cref="MidiError.ParameterUnsupported"/>, если привязка оказалась
    /// битой: это чинится в журнале, но не ломает остальные привязки — одну
    /// неработающую кнопку из двадцати пользователь починит сам, а падение
    /// унесло бы всё остальное.
    /// </summary>
    public MidiResult Execute(MidiMessageInfo message)
    {
        MidiError? firstFailure = null;

        foreach (var binding in _bindings.All)
        {
            if (!MatchesKind(binding, message)) continue;
            if (binding.Channel != message.Channel) continue;

            // Pitch Wheel не несёт номера контроллера — там всё крутится всегда,
            // поэтому сверять номер нельзя, иначе он бы вообще не доходил.
            if (binding.MessageKind != MidiMessageKind.PitchWheel && binding.Control != message.Control)
                continue;

            var descriptor = MidiParameters.All.FirstOrDefault(d => d.Key == binding.Parameter);
            if (descriptor == null)
            {
                // Параметр есть в привязке, но исчез из каталога: так бывает,
                // когда настройки приехали из другой версии приложения.
                _logger.LogWarning(
                    "MIDI: параметр «{Parameter}» не найден в каталоге, привязка ({Target} {Strip}) пропущена",
                    binding.Parameter, binding.TargetType, binding.StripId);
                firstFailure ??= MidiError.ParameterUnsupported;
                continue;
            }

            var result = descriptor.Shape == MidiParamShape.Button
                ? ApplyButton(binding, message)
                : ApplyContinuous(binding, descriptor, message);

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "MIDI: {Error} для {Parameter} ({Target} {Strip}, CC {Control} канал {Channel})",
                    result.Error, binding.Parameter, binding.TargetType,
                    binding.StripId, binding.Control, binding.Channel);
                firstFailure ??= result.Error;
            }
        }

        return firstFailure is { } error ? MidiResult.Fail(error) : MidiResult.Ok;
    }

    /// <summary>
    /// Сопоставляет вид сообщения с привязкой.
    ///
    /// Для привязки к ноте дополнительно принимаем Note Off: отпускание ноты
    /// приходит именно этим событием, а не «Note On с velocity = 0» (хотя
    /// контроллеры шлют и так, и разбирается это уже в порте). Без этой
    /// асимметрии удержание по ноте не отпускалось бы никогда.
    /// </summary>
    private static bool MatchesKind(MidiBinding binding, MidiMessageInfo message)
    {
        if (binding.MessageKind == message.Kind) return true;
        return binding.MessageKind == MidiMessageKind.NoteOn && message.Kind == MidiMessageKind.NoteOff;
    }

    private MidiResult ApplyContinuous(MidiBinding binding, MidiParameterDescriptor descriptor, MidiMessageInfo message)
    {
        float value = MidiValueNormalizer.Normalize(descriptor, message);
        _logger.LogDebug("MIDI {Param} = {Value:F2} ({Target} {Strip})",
            binding.Parameter, value, binding.TargetType, binding.StripId);

        return _strips.SetContinuous(binding.TargetType, binding.StripId, binding.Parameter, value);
    }

    /// <summary>
    /// Кнопочная привязка. Режим определяет, что делать с фронтом нажатия:
    /// Toggle — переключить, Hold — включить и вернуть по отпусканию,
    /// Latch — поставить параметр ровно в присланное контроллером состояние.
    /// </summary>
    private MidiResult ApplyButton(MidiBinding binding, MidiMessageInfo message)
    {
        bool isPressed = IsPressed(binding, message);
        string key = MidiBindingCommand.KeyOf(binding);

        // Пока кнопку держат, сообщения идут пачками. Действие — только на фронте,
        // иначе Toggle щёлкал бы десять раз в секунду.
        if (!_pressed.IsRisingEdge(key, isPressed)) return MidiResult.Ok;

        // Режим кнопки виден только в журнале: по нему понятно, почему параметр
        // переключился (Toggle) или удержался (Hold/Latch).
        _logger.LogInformation("MIDI {Param} [{Mode}] {State} ({Target} {Strip})",
            binding.Parameter, binding.Mode, isPressed ? "нажато" : "отпущено",
            binding.TargetType, binding.StripId);

        return binding.Mode switch
        {
            MidiButtonMode.Hold => ApplyHold(binding, key, isPressed),
            MidiButtonMode.Latch => ApplyLatch(binding, isPressed),
            // Toggle реагирует только на нажатие: отпускание не значит «снова
            // переключить», иначе кнопка работала бы в два нажатия.
            _ => isPressed ? _strips.ToggleButton(binding.TargetType, binding.StripId, binding.Parameter) : MidiResult.Ok
        };
    }

    /// <summary>
    /// Нажата ли кнопка. Для CC обычная полярность — 127 = нажато, но кнопки
    /// с фиксацией на части MIDI-микшеров шлют наоборот (0 = нажато,
    /// 127 = отпущено); такая полярность включается флагом IsInverted у привязки.
    ///
    /// Порог 64, а не 127: контроллер присылает и промежуточные значения, пока
    /// палец идёт по клавише, и ждать строгих 127 значило бы терять первые
    /// сообщения быстрых нажатий.
    /// </summary>
    private static bool IsPressed(MidiBinding binding, MidiMessageInfo message) => message.Kind switch
    {
        MidiMessageKind.NoteOn => message.Value > 0,
        MidiMessageKind.NoteOff => false,
        _ => binding.IsInverted ? message.Value < 64 : message.Value >= 64
    };

    /// <summary>
    /// PTT по удержанию: пока кнопка нажата — параметр активен (Mute — звук
    /// открыт, Solo/Mono/Denoiser — включены), на отпускание возвращается
    /// прежнее состояние.
    /// </summary>
    private MidiResult ApplyHold(MidiBinding binding, string key, bool isPressed)
    {
        if (!MidiStripAccessor.IsButtonParameter(binding.Parameter))
        {
            return MidiResult.Fail(MidiError.ParameterUnsupported);
        }

        if (isPressed)
        {
            var before = _strips.GetButton(binding.TargetType, binding.StripId, binding.Parameter);
            if (!before.IsSuccess) return MidiResult.Fail(before.Error);

            // Состояние ДО нажатия запоминаем, а не «выключено»: если параметр
            // уже был включён, отпускание обязано вернуть включённое.
            _pressed.RememberAndPress(key, before.Value);
            return _strips.SetButton(binding.TargetType, binding.StripId, binding.Parameter,
                MidiStripAccessor.ButtonActiveValue(binding.Parameter));
        }

        return _pressed.ReleaseAndTakeRestore(key, out var restore)
            ? _strips.SetButton(binding.TargetType, binding.StripId, binding.Parameter, restore)
            : MidiResult.Ok;
    }

    /// <summary>
    /// Кнопка с фиксацией: контроллер шлёт не нажатие, а состояние
    /// («включено»/«выключено»), поэтому параметр ставится ровно в это
    /// состояние. Одно нажатие фиксированной кнопки включает микрофон,
    /// следующее — выключает.
    /// </summary>
    private MidiResult ApplyLatch(MidiBinding binding, bool isPressed)
    {
        if (!MidiStripAccessor.IsButtonParameter(binding.Parameter))
        {
            return MidiResult.Fail(MidiError.ParameterUnsupported);
        }

        bool active = MidiStripAccessor.ButtonActiveValue(binding.Parameter);
        bool pressed = isPressed == active;
        return _strips.SetButton(binding.TargetType, binding.StripId, binding.Parameter, pressed);
    }
}