using Microsoft.Extensions.Logging;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Сбрасывает отслеживание нажатий и откатывает зажатые PTT-кнопки.
///
/// Вызывается при каждом изменении MIDI-настроек: сменилось устройство,
/// привязка или её режим. Без сброса первое же сообщение съедалось бы как
/// «уже нажато» — например, новая привязка не сработала бы до первого
/// отпускания, а пользователь решил бы, что Learn сломан.
///
/// Самое важное здесь — откат PTT. Кнопка, нажатая по удержанию, держит
/// параметр включённым, пока не придёт отпускание. Если в этот момент сбросить
/// устройство или снять привязку, отпускания уже не будет никогда, и микрофон
/// останется открытым до перезапуска приложения. Поэтому сброс не «забывает
/// нажатия», а доводит их до конца.
/// </summary>
public sealed class ResetMidiTrackingUseCase
{
    private readonly IMidiBindingStore _bindings;
    private readonly MidiStripAccessor _strips;
    private readonly MidiPressTracker _pressed;
    private readonly ILogger _logger = AppLog.For<ResetMidiTrackingUseCase>();

    public ResetMidiTrackingUseCase(
        IMidiBindingStore bindings,
        MidiStripAccessor strips,
        MidiPressTracker pressed)
    {
        _bindings = bindings;
        _strips = strips;
        _pressed = pressed;
    }

    /// <summary>
    /// Возвращает параметры, зажатые по PTT, в прежнее состояние и забывает
    /// фронты нажатий.
    /// </summary>
    /// <returns>
    /// <see cref="MidiError.StripMissing"/>, если привязка указывала на стрип,
    /// которого уже нет: откатывать нечем, и это стоит записать, потому что
    /// параметр мог остаться включённым навсегда.
    /// </returns>
    public MidiResult Execute()
    {
        MidiError? firstFailure = null;

        foreach (var (bindingKey, valueToRestore) in _pressed.DrainHeldPtt())
        {
            // Ключ несёт в себе (TargetType, StripId, Parameter) — разбираем,
            // чтобы обратиться к стрипу, но ищем привязку по целому ключу:
            // снятая привязка означает «откатывать нечего».
            var binding = _bindings.All.FirstOrDefault(b => MidiBindingCommand.KeyOf(b) == bindingKey);
            if (binding is null) continue;

            var result = _strips.SetButton(binding.TargetType, binding.StripId, binding.Parameter, valueToRestore);
            if (result.IsSuccess) continue;

            _logger.LogWarning(
                "MIDI: не удалось откатить PTT для {Parameter} ({Target} {Strip}): {Error}",
                binding.Parameter, binding.TargetType, binding.StripId, result.Error);
            firstFailure ??= result.Error;
        }

        _pressed.Clear();

        _logger.LogInformation(
            "MIDI-настройки изменены: устройство «{Device}», привязок {Count}",
            _bindings.DeviceName ?? "<не выбрано>", _bindings.All.Count);

        return firstFailure is { } error ? MidiResult.Fail(error) : MidiResult.Ok;
    }
}