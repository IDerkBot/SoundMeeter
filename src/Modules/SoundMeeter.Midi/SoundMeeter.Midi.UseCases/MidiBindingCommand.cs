using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Запрос на изменение MIDI-привязки извне модуля (окно привязок, Learn).
///
/// Приходит снаружи и превращается в доменный <see cref="MidiBinding"/> внутри
/// сценария. Отдельный тип нужен по двум причинам. Первая: Presentation не
/// должен знать, как устроен доменный объект — иначе правка формы привязки
/// разъезжается с настройками. Вторая: сценарий не должен получать «половину»
/// привязки и дособирать недостающее самому, — иначе у каждого сценария свои
/// правила про то, что считать заданным по умолчанию.
/// </summary>
/// <param name="TargetType">"Input" или "Bus".</param>
/// <param name="StripId">Стабильный Id стрипа.</param>
/// <param name="Parameter">Ключ параметра из <see cref="MidiParameters"/>.</param>
public sealed record MidiBindingCommand(
    string TargetType,
    string StripId,
    string Parameter,
    int Channel,
    int Control,
    MidiMessageKind MessageKind,
    MidiButtonMode Mode,
    bool IsInverted)
{
    /// <summary>Ключ привязки в виде строки — для логов и состояния кнопок.</summary>
    public string BindingKey => $"{TargetType}|{StripId}|{Parameter}";

    /// <summary>Тот же ключ для уже существующей привязки.</summary>
    public static string KeyOf(MidiBinding binding) =>
        $"{binding.TargetType}|{binding.StripId}|{binding.Parameter}";

    /// <summary>Доменный объект, который положим в хранилище.</summary>
    public MidiBinding ToBinding() => new()
    {
        TargetType = TargetType,
        StripId = StripId,
        Parameter = Parameter,
        Channel = Channel,
        Control = Control,
        MessageKind = MessageKind,
        Mode = Mode,
        IsInverted = IsInverted
    };
}