using Microsoft.Extensions.Logging;
using NAudio.Midi;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services;

public sealed class MidiService : IMidiService
{
    private readonly ILogger _logger = AppLog.For<MidiService>();
    private MidiIn? _midiIn;

    public IReadOnlyList<MidiInputDevice> Devices { get; }

    public bool IsOpen => _midiIn != null;

    public event Action<MidiMessageInfo>? MessageReceived;

    public MidiService()
    {
        var list = new List<MidiInputDevice>();
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                try
                {
                    var caps = MidiIn.DeviceInfo(i);
                    list.Add(new MidiInputDevice(i, caps.ProductName));
                }
                catch
                {
                    list.Add(new MidiInputDevice(i, $"MIDI {i}"));
                }
            }
        }
        catch
        {
            // MIDI-подсистема недоступна — пустой список.
        }
        Devices = list;
    }

    public void Open(string? deviceName)
    {
        Close();

        if (string.IsNullOrWhiteSpace(deviceName)) return;

        // Открываем именно сохранённое устройство. Если его нет в системе —
        // не подхватываем первое попавшееся, а просто остаёмся закрытыми.
        if (Devices.FirstOrDefault(d => d.Name == deviceName) is not { } device)
        {
            _logger.LogWarning("MIDI-устройство «{Device}» не найдено среди {Count} подключённых",
                deviceName, Devices.Count);
            return;
        }

        try
        {
            _midiIn = new MidiIn(device.Index);
            _midiIn.MessageReceived += OnMessageReceived;
            _midiIn.Start();
            _logger.LogInformation("MIDI открыт: {Device} (index {Index})", device.Name, device.Index);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось открыть MIDI-устройство «{Device}»: {Message}",
                device.Name, ex.Message);
            Close();
        }
    }

    public void Close()
    {
        if (_midiIn == null) return;
        try
        {
            _midiIn.MessageReceived -= OnMessageReceived;
            _midiIn.Stop();
            _midiIn.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка закрытия MIDI: {Message}", ex.Message);
        }
        _midiIn.Dispose();
        _midiIn = null;
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        try
        {
            var info = Parse(e.RawMessage);
            if (info == null) return;

            // MIDI-сообщения — единственное место, где видно, что контроллер
            // действительно прислал событие: без записи «кнопка не реагирует»
            // нечем объяснить, кроме догадок.
            _logger.LogDebug("MIDI {Kind} ch={Channel} cc={Control} value={Value}",
                info.Value.Kind, info.Value.Channel, info.Value.Control, info.Value.Value);
            MessageReceived?.Invoke(info.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Некорректное MIDI-сообщение 0x{Raw:X8}: {Message}", e.RawMessage, ex.Message);
        }
    }

    /// <summary>Разбирает короткое MIDI-сообщение вручную (без зависимостей от класса события).</summary>
    internal static MidiMessageInfo? Parse(int rawMessage)
    {
        int status = rawMessage & 0xFF;
        int channel = status & 0x0F;
        int command = status & 0xF0;
        int data1 = (rawMessage >> 8) & 0xFF;
        int data2 = (rawMessage >> 16) & 0xFF;

        switch (command)
        {
            case 0xB0: // Control Change: CC = data1, value = data2
                return new MidiMessageInfo(MidiMessageKind.ControlChange, channel, data1, data2);
            case 0x90 when data2 > 0: // Note On с velocity>0 — нажатие
                return new MidiMessageInfo(MidiMessageKind.NoteOn, channel, data1, data2);
            case 0x90: // Note On с velocity=0 — отпускание (running-status NoteOff)
                return new MidiMessageInfo(MidiMessageKind.NoteOff, channel, data1, data2);
            case 0x80: // Note Off — отпускание
                return new MidiMessageInfo(MidiMessageKind.NoteOff, channel, data1, data2);
            case 0xE0: // Pitch Wheel: 14-бит value = data1 | data2<<7
                return new MidiMessageInfo(MidiMessageKind.PitchWheel, channel, 0, data1 | (data2 << 7));
            default:
                return null; // послекасание / прочее — игнорируем
        }
    }

    public void Dispose() => Close();
}