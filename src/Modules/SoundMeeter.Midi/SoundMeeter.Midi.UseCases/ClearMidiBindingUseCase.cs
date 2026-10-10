using Microsoft.Extensions.Logging;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Midi.Contract;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Снимает привязку: параметр перестаёт реагировать на контроллер.
///
/// Снятие — это не только забыть, но и разорвать текущее удержание: если
/// параметр был зажат по PTT, без отката он остался бы включённым навсегда,
/// потому что отпускания уже не придёт — привязки, по которой оно пришло,
/// больше нет. Поэтому откат идёт ДО удаления записи из хранилища.
/// </summary>
public sealed class ClearMidiBindingUseCase
{
    private readonly IMidiBindingStore _bindings;
    private readonly ResetMidiTrackingUseCase _resetTracking;
    private readonly ILogger _logger = AppLog.For<ClearMidiBindingUseCase>();

    public ClearMidiBindingUseCase(IMidiBindingStore bindings, ResetMidiTrackingUseCase resetTracking)
    {
        _bindings = bindings;
        _resetTracking = resetTracking;
    }

    /// <summary>
    /// Снимает привязку параметра.
    /// </summary>
    /// <returns>
    /// Штатно и если привязки не было. Иначе пользователь, уже снявший кнопку
    /// вручную и дёрнувший «очистить» ещё раз, получил бы ошибку вместо
    /// ожидаемого «уже чисто».
    /// </returns>
    public MidiResult Execute(string targetType, string stripId, string parameter)
    {
        if (_bindings.Find(targetType, stripId, parameter) is null) return MidiResult.Ok;

        // Откат PTT обязан случиться, пока привязка ещё есть в хранилище: после
        // удаления сброс не найдёт её и параметр останется в состоянии нажатия.
        _resetTracking.Execute();

        _bindings.Remove(targetType, stripId, parameter);

        _logger.LogInformation("MIDI: привязка снята — {Target} {Strip} {Parameter}",
            targetType, stripId, parameter);

        return MidiResult.Ok;
    }
}