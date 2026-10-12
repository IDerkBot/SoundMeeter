namespace SoundMeeter.Midi.Entities;

/// <summary>
/// Перевод значения MIDI-сообщения в шкалу параметра.
///
/// Контроллер присылает 0…127 (или 0…16383 у Pitch Wheel), а параметр живёт в
/// своих единицах: громкость — −60…+12 дБ, подавление шума — 0…100. Разница
/// разная у каждого параметра, поэтому приводить «на лету» в обработчике
/// нельзя — пришлось бы повторять Min/Max в коде применения, где их легко
/// перепутать между фейдером и крутилкой.
///
/// Pitch Wheel нормализуется по 16383, а не по 127 сознательно: это
/// 14-битное значение, и делить его на 127 «как остальные» отдавало бы
/// контроллеру только нижние 128 значений из 16384 — то есть wheel работал
/// бы на первой восьмой своей шкалы.
/// </summary>
public static class MidiValueNormalizer
{
    /// <summary>Верхняя граница значения обычного сообщения (CC, нота).</summary>
    public const int MaxStandard = 127;

    /// <summary>Верхняя граница значения Pitch Wheel (14 бит).</summary>
    public const int MaxPitchWheel = 16383;

    /// <summary>
    /// Значение сообщения в шкале параметра: <c>Min + (Max - Min) * normalized</c>.
    /// </summary>
    public static float Normalize(MidiParameterDescriptor descriptor, MidiMessageInfo message)
    {
        int max = message.Kind == MidiMessageKind.PitchWheel ? MaxPitchWheel : MaxStandard;

        // Значение вне диапазона — это не наша ошибка и не повод бросать:
        // некоторые контроллеры присылают 127 на месте 126 и т.п. Зажимаем, иначе
        // фейдер уехал бы за пределы шкалы и остался там до следующего сообщения.
        float normalized = Math.Clamp((float)message.Value / max, 0f, 1f);

        return descriptor.Min + (descriptor.Max - descriptor.Min) * normalized;
    }
}