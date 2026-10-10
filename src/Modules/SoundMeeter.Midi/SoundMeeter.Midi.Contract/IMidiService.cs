using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.Contract;

/// <summary>
/// Сервис доступа к MIDI-устройству ввода.
///
/// Контракт отдельно от реализации (<c>Midi.Contract.Implementation</c>) и от
/// ViewModel (<c>Midi.UseCases</c>): проверять привязки можно с заглушкой,
/// не имея контроллера под рукой.
/// </summary>
public interface IMidiService : IDisposable
{
    /// <summary>Доступные MIDI-устройства ввода (Index + ProductName).</summary>
    IReadOnlyList<MidiInputDevice> Devices { get; }

    bool IsOpen { get; }

    /// <summary>Открывает устройство по ProductName (null — закрыть).</summary>
    void Open(string? deviceName);

    void Close();

    /// <summary>Приходит с потока MIDI — НЕ с UI-потока. Нужен маршалинг на Dispatcher.</summary>
    event Action<MidiMessageInfo>? MessageReceived;
}
