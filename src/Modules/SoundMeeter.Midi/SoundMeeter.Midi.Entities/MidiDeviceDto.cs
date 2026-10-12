namespace SoundMeeter.Midi.Entities;

/// <summary>
/// Пункт выпадающего списка MIDI-устройств, готовый к показу.
/// <param name="Device">Устройство, если оно подключено сейчас; null — «выключено».</param>
public sealed record MidiDeviceDto(MidiInputDevice? Device)
{
    /// <summary>Пункт «выключено».</summary>
    public static MidiDeviceDto None { get; } = new((MidiInputDevice?)null);

    public bool IsNone => Device is null;

    /// <summary>Имя для показа. Само имя приходит из домена — переводом строк
    /// занимается Presentation, у него уже есть <c>Sm.Midi.None</c>.</summary>
    public string Name => Device?.Name ?? string.Empty;

    /// Список для выпадающего списка: сначала «выключено», затем подключённые
    /// устройства в порядке, в котором их сообщил порт.
    /// </summary>
    public static IReadOnlyList<MidiDeviceDto> BuildFor(IReadOnlyList<MidiInputDevice> devices)
    {
        var list = new List<MidiDeviceDto>(devices.Count + 1) { None };
        foreach (var device in devices)
            list.Add(new MidiDeviceDto(device));
        return list;
    }

    /// <summary>
    /// Какой пункт выбрать в списке, если в настройках сохранено <paramref name="savedName"/>.
    ///
    /// Правило, а не строка выбора: сохранённое устройство может быть отключено.
    /// Тогда показываем «выключено», но имя НЕ стираем — иначе контроллер,
    /// переподключённый после перезапуска, пришлось бы выбирать заново каждый раз.
    /// Именно поэтому здесь <c>null</c>, а не «первое доступное устройство»:
    /// молча подхватить чужой контроллер и писать в его параметры опаснее, чем
    /// промолчать.
    ///
    /// Строка сравнения регистронезависимая: ProductName приходит из драйвера, и
    /// на другой машине регистр может отличаться.
    /// </summary>
    public static MidiDeviceDto Resolve(IReadOnlyList<MidiDeviceDto> options, string? savedName)
    {
        if (string.IsNullOrWhiteSpace(savedName)) return None;

        foreach (var option in options)
        {
            if (option.Device is null) continue;
            if (string.Equals(option.Name, savedName, StringComparison.OrdinalIgnoreCase))
                return option;
        }

        return None;
    }
}