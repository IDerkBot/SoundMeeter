namespace SoundMeeter.Midi.Entities;

/// <summary>
/// MIDI-устройство ввода, найденное при опросе системы.
///
/// Индекс намеренно едет вместе с именем: в привязках сохраняется имя
/// (ProductName читает человек), а открывать устройство нужно по индексу —
/// только NAudio его и принимает.
/// </summary>
public sealed record MidiInputDevice(int Index, string Name)
{
    /// <summary>
    /// Устройство, известное только по имени, — сохранённое в настройках, но
    /// сейчас не подключённое. Индекс неизвестен и равен -1: открывать его
    /// нельзя, оно нужно только чтобы показать пользователю, что привязки
    /// сохранены и заработают, когда контроллер вернётся.
    /// </summary>
    public static MidiInputDevice FromName(string name) => new(-1, name);

    /// <summary>Устройство из этого списка подключено прямо сейчас.</summary>
    public bool IsConnected => Index >= 0;
}
