using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Presentation.Helpers;

/// <summary>
/// Пункт выпадающего списка MIDI-устройств для UI.
///
/// Обёртка над <see cref="MidiDeviceDto"/>: <c>ToString</c> для ComboBox, ключ
/// локализации для «выключено» и локализуемое имя. Само решение «какое
/// устройство считать выбранным при отключённом контроллере» в DTO не переехало
/// намеренно — это доменное правило, и оно описано там, где его можно проверить.
/// </summary>
public sealed class MidiDeviceOption : LocalizedViewModel
{
    /// <summary>Устройство, если оно подключено сейчас; null — «выключено».</summary>
    public MidiDeviceOption(MidiDeviceDto value) => Value = value;

    public MidiDeviceDto Value { get; }

    public bool IsNone => Value.IsNone;

    /// <summary>Имя устройства для передачи в сценарий выбора.</summary>
    public string Name => Value.Name;

    /// <summary>
    /// Подпись в списке. Имя устройства приезжает из драйвера и переводить его
    /// нельзя, а вот «выключено» — надпись приложения, поэтому она идёт через
    /// Loc.
    /// </summary>
    public string Display => IsNone ? Loc.Get("Sm.Midi.None") : Value.Name;

    public override string ToString() => Display;
}