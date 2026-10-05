using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Заглушка движка: ViewModel'ы проверяются без WASAPI, единственное, что
/// интересует тесты, — какие команды роутинга доехали до движка и в каком
/// порядке. Реальный <c>WasapiAudioEngine</c> для этого не годится: он
/// трогает устройства и требует живого аудиопотока, а здесь нужен чистый
/// список вызовов.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public List<(string BusId, bool Enabled)> Routes { get; } = new();

    /// <summary>Вызовы SetRouteGain — для проверок посылки.</summary>
    public List<(string BusId, float GainDb)> RouteGains { get; } = new();

    public IReadOnlyList<InputChannelModel> Inputs { get; } = new List<InputChannelModel>();
    public IReadOnlyList<OutputBusModel> Buses { get; } = new List<OutputBusModel>();
    public IReadOnlyList<DeviceInfo> Catalog { get; } = new List<DeviceInfo>();
    public bool IsRunning => false;
    public MidiSettings Midi { get; set; } = new();

    // События объявлены, потому что их требует интерфейс: тесты их не слушают,
    // но их наличие проверяет, что заглушка не разошлась с движком.
#pragma warning disable CS0067
    public event Action? ChannelsChanged;
    public event Action? StateChanged;
#pragma warning restore CS0067

    public void SetRoute(string inputId, string busId, bool enabled)
    {
        Routes.RemoveAll(r => r.BusId == busId);
        Routes.Add((busId, enabled));
    }

    public void SetRouteGain(string inputId, string busId, float gainDb) =>
        RouteGains.Add((busId, gainDb));

    public void SetInputSolo(string inputId, bool value) { }
    public void SetOutputSolo(string busId, bool value) { }
    public void RefreshDevices() { }
    public void Start() { }
    public void Stop() { }
    public void AddInput() { }
    public void RemoveInput(string inputId) { }
    public void AddBus() { }
    public void RemoveBus(string busId) { }
    public void MoveInput(int fromIndex, int toIndex) { }
    public void MoveBus(int fromIndex, int toIndex) { }
    public void SetInputSource(string inputId, string? deviceId) { }
    public void SetInputAppTarget(string inputId, string? deviceId) { }
    public void SetBusSource(string busId, string? deviceId) { }
    public AppSettings CreateSnapshot() => new();
    public void ApplyPreset(AppSettings preset) { }
    public void EnsureRoutingTable() { }
    public void Dispose() { }
}
