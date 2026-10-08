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

    /// <summary>
    /// Стрипы, которые «есть» в микшере. По умолчанию пусто — ровно как при
    /// первом запуске, когда полосы заводит только пользователь.
    /// </summary>
    public List<InputChannelModel> FakeInputs { get; } = new();

    public IReadOnlyList<InputChannelModel> Inputs => FakeInputs;
    public IReadOnlyList<OutputBusModel> Buses { get; } = new List<OutputBusModel>();
    public IReadOnlyList<DeviceInfo> Catalog { get; } = new List<DeviceInfo>();

    /// <summary>
    /// Движок «запущен». Подаётся в том числе модулю синтеза речи: озвучка
    /// принимается только работающим микшером, и это его решение проверяется.
    /// </summary>
    public bool AcceptAudio { get; set; }

    public bool IsRunning => AcceptAudio;

    public MidiSettings Midi { get; set; } = new();

    /// <summary>Сэмплы, которые доехали до стрипов, с их Id.</summary>
    public List<(string InputId, int Samples)> PushedAudio { get; } = new();

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
    public void Start() => AcceptAudio = true;
    public void Stop() => AcceptAudio = false;
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

    /// <summary>
    /// Создаёт генерируемый стрип — ровно так же, как настоящий движок: тот же
    /// признак, та же доступность, тот же вызов ChannelsChanged. Повторный запрос
    /// с тем же именем возвращает существующий канал, поэтому тесты не плодят
    /// копии.
    /// </summary>
    public string EnsureGeneratedInput(string name)
    {
        var existing = FakeInputs.FirstOrDefault(input =>
            input.IsGenerated && string.Equals(input.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        var created = new InputChannelModel
        {
            Name = name,
            ChannelName = name,
            IsGenerated = true,
            IsAvailable = true,
        };

        FakeInputs.Add(created);
        ChannelsChanged?.Invoke();
        return created.Id;
    }

    /// <summary>
    /// Принимает сэмплы только работающим движком: у настоящего источника есть
    /// тогда, когда канал куда-то послан, и модуль синтеза на этом и должен
    /// останавливаться, не тратя секунды на фразу, которую никто не услышит.
    ///
    /// Принятое копится и тает в реальном времени — так же, как настоящий буфер
    /// источника. Заглушка без этого обманывала бы модуль: он ждал бы конца
    /// фразы, которой никогда не будет, и очередь встала бы после первой реплики.
    /// </summary>
    public bool PushAudio(string inputId, ReadOnlySpan<float> samples)
    {
        if (!AcceptAudio || samples.IsEmpty) return false;

        PushedAudio.Add((inputId, samples.Length));

        lock (_bufferedGate)
        {
            DrainBuffered();
            _bufferedSamples += samples.Length;
        }

        return true;
    }

    private long _bufferedSamples;
    private long _bufferedUpdatedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly object _bufferedGate = new();

    /// <summary>
    /// Сколько секунд принятого звука ещё не «прозвучало». Тает само, по часам:
    /// отдельный поток в заглушке не нужен, а поток озвучки модуля всё равно опрашивает
    /// это число.
    /// </summary>
    public double BufferedAudioSeconds
    {
        get
        {
            lock (_bufferedGate)
            {
                DrainBuffered();
                return _bufferedSamples / (2.0 * 48000.0);
            }
        }
    }

    public double GetBufferedSeconds(string inputId) => BufferedAudioSeconds;

    public bool ClearBufferedAudio(string inputId)
    {
        lock (_bufferedGate)
        {
            bool had = _bufferedSamples > 0;
            _bufferedSamples = 0;
            _bufferedUpdatedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            return had;
        }
    }

    /// <summary>Снимает с остатка всё, что «прозвучало» с прошлого опроса.</summary>
    private void DrainBuffered()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = (now - _bufferedUpdatedAtTicks) / (double)System.Diagnostics.Stopwatch.Frequency;

        if (elapsed > 0)
        {
            _bufferedSamples = Math.Max(0, _bufferedSamples - (long)(elapsed * 2 * 48000));
            _bufferedUpdatedAtTicks = now;
        }
    }

    public void Dispose() { }
}
