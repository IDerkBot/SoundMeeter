using Microsoft.Extensions.Logging;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Запоминает привязку параметра к нажатой кнопке контроллера (Learn).
///
/// Сценарий «пользователь ткнул кнопку на контроллере, и мы запомнили, что это
/// громкость первого входа». Повторная привязка того же параметра заменяет
/// прежнюю, а не добавляет вторую: один параметр нельзя держать на двух
/// кнопках, иначе переучивание второй молча отвязывало бы первую.
///
/// Порядок шагов здесь важен: сначала откат зажатых PTT (через трекер), потом
/// запись в хранилище. Обратный порядок оставил бы PTT-кнопку «залипшей» на
/// параметре, который уже перестали привязывать.
/// </summary>
public sealed class BindMidiControlUseCase
{
    private readonly IMidiBindingStore _bindings;
    private readonly ResetMidiTrackingUseCase _resetTracking;
    private readonly ILogger _logger = AppLog.For<BindMidiControlUseCase>();

    public BindMidiControlUseCase(IMidiBindingStore bindings, ResetMidiTrackingUseCase resetTracking)
    {
        _bindings = bindings;
        _resetTracking = resetTracking;
    }

    /// <summary>
    /// Привязывает параметр к сообщению контроллера.
    /// </summary>
    /// <returns>Снимок сохранённой привязки для показа в окне.</returns>
    public MidiResult<MidiBinding> Execute(MidiBindingCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.TargetType) ||
            string.IsNullOrWhiteSpace(command.StripId) ||
            string.IsNullOrWhiteSpace(command.Parameter))
        {
            return MidiResult<MidiBinding>.Fail(MidiError.ParameterUnsupported);
        }

        // Привязать можно только параметр, который есть в каталоге: иначе в
        // настройки попадёт строка, которую потом не отобразит ни одно окно.
        bool knownParameter = MidiParameters.All.Any(d => d.Key == command.Parameter);
        bool allowedForTarget = command.TargetType == MidiTargetTypes.Input
            ? MidiParameters.ForInput().Any(d => d.Key == command.Parameter)
            : command.TargetType == MidiTargetTypes.Bus && MidiParameters.ForBus().Any(d => d.Key == command.Parameter);

        if (!knownParameter || !allowedForTarget)
        {
            _logger.LogWarning(
                "MIDI: параметр «{Parameter}» нельзя привязать к {Target} — привязка отклонена",
                command.Parameter, command.TargetType);
            return MidiResult<MidiBinding>.Fail(MidiError.ParameterUnsupported);
        }

        _resetTracking.Execute();

        var binding = command.ToBinding();
        _bindings.Replace(binding);

        _logger.LogInformation(
            "MIDI: {Target} {Strip} {Parameter} ← {Kind} ch{Channel} cc{Control} [{Mode}]{Inverted}",
            binding.TargetType, binding.StripId, binding.Parameter, binding.MessageKind,
            binding.Channel, binding.Control, binding.Mode,
            binding.IsInverted ? ", обратная полярность" : string.Empty);

        return MidiResult<MidiBinding>.Ok(binding);
    }
}