namespace SoundMeeter.Midi.Entities;

/// <summary>
/// Куда направлена привязка: на входной стрип или на выходную шину.
///
/// Строки вместо перечисления не случайны: <c>TargetType</c> пишется в
/// settings.json, и смена представления (перечисление с числовыми значениями)
/// сломала бы сохранённые настройки у всех, кто обновляет приложение. Но
/// «Input»/«Bus» как голые строки расходились по трём файлам — привязка,
/// маршрутизация сообщения и список параметров, — и любая из них могла
/// разойтись с остальными молча: привязка просто перестала бы работать.
/// </summary>
public static class MidiTargetTypes
{
    /// <summary>Входной стрип (микрофон, loopback, виртуальный кабель).</summary>
    public const string Input = "Input";

    /// <summary>Выходная шина.</summary>
    public const string Bus = "Bus";

    /// <summary>Название, понятное в журнале: «Input» вместо 0/1 у enum.</summary>
    public static bool IsKnown(string? targetType) =>
        targetType is Input or Bus;
}