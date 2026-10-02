using SoundMeeter.Models;

namespace SoundMeeter.Services;

/// <summary>MIDI-сообщение в разобранном виде (для привязок).</summary>
public readonly record struct MidiMessageInfo(MidiMessageKind Kind, int Channel, int Control, int Value);

/// <summary>Сервис доступа к MIDI-устройству ввода.</summary>
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

/// <summary>MIDI-устройство ввода.</summary>
public sealed record MidiInputDevice(int Index, string Name);