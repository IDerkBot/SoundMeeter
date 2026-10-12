using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Presentation.Dto;

/// <summary>
/// Привязка, готовая к показанию: снимок доменного объекта для окна.
///
/// Иммутабельный record вместо <see cref="MidiBinding"/> — по двум причинам.
/// Presentation получает снимок и не может случайно изменить настройку в обход
/// сценария. И копия переживает перечитывание коллекции: привязки в
/// настройках — это <c>List&lt;MidiBinding&gt;</c>, и ссылка на элемент
/// становится битой, как только список пересобрали (например, снятие другой
/// привязки пересобирает его целиком).
/// </summary>
public sealed record MidiBindingDto(
    string TargetType,
    string StripId,
    string Parameter,
    int Channel,
    int Control,
    MidiMessageKind MessageKind,
    MidiButtonMode Mode,
    bool IsInverted)
{
    /// <summary>Снимок доменной привязки.</summary>
    public static MidiBindingDto From(MidiBinding binding) => new(
        binding.TargetType,
        binding.StripId,
        binding.Parameter,
        binding.Channel,
        binding.Control,
        binding.MessageKind,
        binding.Mode,
        binding.IsInverted);

    public static MidiBindingDto From(MidiBindingRecord binding) => new(
        binding.TargetType,
        binding.StripId,
        binding.ParameterKey,
        binding.Channel,
        binding.Control,
        binding.Kind,
        binding.Mode,
        binding.IsInverted);

    public string BindingKey => $"{TargetType}|{StripId}|{Parameter}";
}