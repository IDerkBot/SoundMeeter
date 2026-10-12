using SoundMeeter.Audio.Contract;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Contract.Implementation;

/// <summary>
/// Хранилище привязок поверх настроек движка.
///
/// Настройки MIDI живут в <see cref="MidiSettings"/> на <see cref="IAudioEngine"/>
/// и меняются заменой всего объекта (так сохраняет пресет). Поэтому список
/// привязок приходится перечитывать на каждый чих, а не держать в поле — этот
/// класс так и делает: открывает <c>engine.Midi.Bindings</c> заново на каждый
/// вызов. Кэш здесь был бы ловушкой: он молча разошёлся бы с настройками
/// после импорта пресета, и привязки перестали бы применяться.
///
/// Все списки здесь — копии. Наружу отдаётся снимок, поэтому вызывающий не
/// может изменить настройки в обход сценариев, а список не «исчезнет» под
/// ногами у Learn, который держит его между нажатием и приходом сообщения.
/// </summary>
public sealed class MidiBindingStore : IMidiBindingStore
{
    private readonly IAudioEngine _engine;

    public MidiBindingStore(IAudioEngine engine) => _engine = engine;

    /// <summary>Снимок списка привязок в текущем виде.</summary>
    public IReadOnlyList<MidiBinding> All => _engine.Midi.Bindings.ToList();

    /// <summary>
    /// Имя сохранённого устройства. Присваивание только записывает в настройки:
    /// открывать и закрывать порт — дело сценария выбора устройства, а не
    /// хранилища. Иначе смена имени в настройках молча открыла бы MIDI как
    /// побочный эффект обычной записи.
    /// </summary>
    public string? DeviceName
    {
        get => _engine.Midi.DeviceName;
        set => _engine.Midi.DeviceName = value;
    }

    public MidiBinding? Find(string targetType, string stripId, string parameter) =>
        _engine.Midi.Bindings.FirstOrDefault(b =>
            b.TargetType == targetType && b.StripId == stripId && b.Parameter == parameter);

    /// <summary>
    /// Кладёт привязку, заменяя прежнюю с тем же ключом. Ключ — это
    /// (TargetType, StripId, Parameter), а не канал с контроллером: один и тот
    /// же параметр нельзя держать на двух кнопках, иначе обе будут им
    /// управлять, а переучивание второй первой просто потеряет первую.
    /// </summary>
    public void Replace(MidiBinding binding)
    {
        var bindings = _engine.Midi.Bindings;
        Remove(bindings, binding.TargetType, binding.StripId, binding.Parameter);
        bindings.Add(binding);
    }

    public void Remove(string targetType, string stripId, string parameter) =>
        Remove(_engine.Midi.Bindings, targetType, stripId, parameter);

    private static void Remove(List<MidiBinding> bindings, string targetType, string stripId, string parameter) =>
        bindings.RemoveAll(b =>
            b.TargetType == targetType && b.StripId == stripId && b.Parameter == parameter);
}