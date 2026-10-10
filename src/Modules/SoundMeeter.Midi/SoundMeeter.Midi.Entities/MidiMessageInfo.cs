namespace SoundMeeter.Midi.Entities;

/// <summary>
/// MIDI-сообщение в разобранном виде — то, что приходит от контроллера.
///
/// Плоский снимок вместо NAudio-событий: сторонние типы остаются за пределами
/// модуля, а привязки работают с четырьмя полями, которые действительно
/// читают. Разобранное, а не сырое 32-битное слово, потому что «рюшить»
/// статус-байт приходится в одном месте — в реализации сервиса.
/// </summary>
/// <param name="Kind">Вид сообщения: CC, нота, отпускание ноты, PitchWheel.</param>
/// <param name="Channel">Канал 0…15 (0-based).</param>
/// <param name="Control">Номер контроллера или ноты.</param>
/// <param name="Value">Положение/нажатие: 0…127 для CC и нот, 0…16383 для PitchWheel.</param>
public readonly record struct MidiMessageInfo(MidiMessageKind Kind, int Channel, int Control, int Value);
