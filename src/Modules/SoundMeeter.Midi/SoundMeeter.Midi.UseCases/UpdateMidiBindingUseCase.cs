using Microsoft.Extensions.Logging;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Меняет режим кнопки и обратную полярность у существующей привязки.
///
/// Режим (Toggle / Hold / Latch) и полярность — настройки самой привязки, а не
/// параметра стрипа: одна и та же громкость может быть на обычной кнопке и на
/// кнопке с фиксацией. Поэтому меняются они здесь, а не в каталоге параметров.
///
/// Отдельный сценарий, а не обновление «привязки целиком», потому что
/// переучивать кнопку по Learn пользователь не просил. Такой вызов пришлось бы
/// откатывать, если бы новый канал или контроллер не совпали с текущими, —
/// и лишний код обратного отката учил бы нас не доверять собственному API.
/// </summary>
public sealed class UpdateMidiBindingUseCase
{
    private readonly IMidiBindingStore _bindings;
    private readonly ResetMidiTrackingUseCase _resetTracking;
    private readonly ILogger _logger = AppLog.For<UpdateMidiBindingUseCase>();

    public UpdateMidiBindingUseCase(IMidiBindingStore bindings, ResetMidiTrackingUseCase resetTracking)
    {
        _bindings = bindings;
        _resetTracking = resetTracking;
    }

    /// <summary>
    /// Переключает режим кнопки.
    ///
    /// Нет-op, если режим уже такой: иначе смена языка или перерисовка окна
    /// гоняла бы откат PTT без причины, а в журнале сыпались бы записи о
    /// несуществующих изменениях.
    /// </summary>
    public MidiResult SetMode(string targetType, string stripId, string parameter, MidiButtonMode mode)
    {
        var binding = _bindings.Find(targetType, stripId, parameter);
        if (binding is null)
        {
            _logger.LogWarning(
                "MIDI: режим кнопки не применён — привязки {Target} {Strip} {Parameter} нет",
                targetType, stripId, parameter);
            return MidiResult.Fail(MidiError.ParameterUnsupported);
        }

        if (binding.Mode == mode) return MidiResult.Ok;

        binding.Mode = mode;
        _resetTracking.Execute();

        _logger.LogInformation(
            "MIDI: {Target} {Strip} {Parameter} — режим кнопки {Mode}",
            targetType, stripId, parameter, mode);

        return MidiResult.Ok;
    }

    /// <summary>
    /// Обратная полярность CC: 0 = нажато, 127 = отпущено.
    ///
    /// Сбрасывает отслеживание нажатий обязательно. Полярность меняет смысл
    /// порога, и без сброса кнопка, зажатая до переключения, осталась бы
    /// «нажатой» или «отпущенной» не по новым правилам — и первое же нажатие
    /// не сработало бы.
    /// </summary>
    public MidiResult SetInverted(string targetType, string stripId, string parameter, bool inverted)
    {
        var binding = _bindings.Find(targetType, stripId, parameter);
        if (binding is null)
        {
            _logger.LogWarning(
                "MIDI: полярность не применена — привязки {Target} {Strip} {Parameter} нет",
                targetType, stripId, parameter);
            return MidiResult.Fail(MidiError.ParameterUnsupported);
        }

        if (binding.IsInverted == inverted) return MidiResult.Ok;

        binding.IsInverted = inverted;
        _resetTracking.Execute();

        _logger.LogInformation(
            "MIDI: {Target} {Strip} {Parameter} — обратная полярность {State}",
            targetType, stripId, parameter, inverted ? "включена" : "выключена");

        return MidiResult.Ok;
    }
}