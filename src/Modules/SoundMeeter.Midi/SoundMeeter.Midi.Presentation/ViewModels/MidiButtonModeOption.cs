using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Presentation.ViewModels;

/// <summary>
/// Пункт выпадающего списка режима кнопки. Название берётся из ресурсов по ключу,
/// поэтому переключение языка обновляет и его (список пересоздаётся заново).
/// </summary>
public sealed class MidiButtonModeOption
{
    public MidiButtonModeOption(MidiButtonMode value, string nameKey)
    {
        Value = value;
        NameKey = nameKey;
    }

    public MidiButtonMode Value { get; }

    public string NameKey { get; }

    public string Name => Loc.Get(NameKey);

    public override string ToString() => Name;
}
