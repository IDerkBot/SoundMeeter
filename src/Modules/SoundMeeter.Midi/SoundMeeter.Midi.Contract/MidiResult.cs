using SoundMeeter.Midi.Entities;
using System.Diagnostics.CodeAnalysis;

namespace SoundMeeter.Midi.Contract;

/// <summary>
/// Исход сценария MIDI без данных.
///
/// Модульный тип, а не общий <c>Result&lt;T&gt;</c> на решение: у MIDI свой
/// перечень состояний (<see cref="MidiError"/>) и своя карта ключей локализации.
/// Общий тип потребовал бы либо пустого «прочие» для модулей, которым он не
/// нужен, либо правки чужих модулей ради одного. Когда появятся вторые и третьи
/// модули с одинаковым набором исходов — тогда это общий тип в Framework.Base,
/// и этот файл удалится целиком.
/// </summary>
public readonly record struct MidiResult
{
    private MidiResult(MidiError error) => Error = error;

    /// <summary>Что не так: <see cref="MidiError.None"/> — всё штатно.</summary>
    public MidiError Error { get; }

    public bool IsSuccess => Error == MidiError.None;

    /// <summary>
    /// Ключ строки в <c>Resources/Strings.resx</c> для этого исхода. Само
    /// сообщение не собирается здесь намеренно: перевод — забота Presentation,
    /// а этот слой не должен зависеть от ChangeLanguage.
    /// </summary>
    public string MessageKey => Error switch
    {
        MidiError.DeviceNotFound => "Sm.Midi.Error.DeviceNotFound",
        MidiError.DeviceOpenFailed => "Sm.Midi.Error.DeviceOpenFailed",
        MidiError.StripMissing => "Sm.Midi.Error.StripMissing",
        MidiError.ParameterUnsupported => "Sm.Midi.Error.ParameterUnsupported",
        _ => string.Empty
    };

    public static MidiResult Ok => new(MidiError.None);

    public static MidiResult Fail(MidiError error) => error switch
    {
        MidiError.None => throw new ArgumentOutOfRangeException(nameof(error),
            "Ошибка None — это не ошибка: для штатного исхода есть Ok."),
        _ => new MidiResult(error)
    };
}

/// <summary>
/// Исход сценария с данными. Значение есть только при <see cref="IsSuccess"/>:
/// ошибка «устройство занято» не должна возвращать DTO пустого устройства,
/// выдавая его за удачный выбор.
/// </summary>
public readonly record struct MidiResult<T>
{
    private MidiResult(MidiError error, T? value)
    {
        Error = error;
        _value = value;
    }

    public MidiError Error { get; }

    private readonly T? _value;

    public bool IsSuccess => Error == MidiError.None;

    /// <summary>
    /// Результат сценария. Бросает <see cref="InvalidOperationException"/>, если
    /// исход ошибочный: читать значение неудачного сценария — ошибка в коде
    /// вызывающего, и молча вернуть <c>default</c> значит отложить её до
    /// места, где отличить «пусто» от «ошибка» уже невозможно.
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Значение недоступно: сценарий MIDI завершился с ошибкой {Error}");

    public string MessageKey => MidiResult.Fail(Error).MessageKey;

    public static MidiResult<T> Ok(T value) => new(MidiError.None, value);

    public static MidiResult<T> Fail(MidiError error) => error switch
    {
        MidiError.None => throw new ArgumentOutOfRangeException(nameof(error),
            "Ошибка None — это не ошибка: для штатного исхода есть Ok."),
        _ => new MidiResult<T>(error, default)
    };

    /// <summary>Попытка достать значение без исключения.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess && value is not null;
    }
}