using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Источник стрипа, который наполняет код приложения, а не устройство.
/// Кольцо, курсоры, тапы и вся последующая обработка у него — те же, что у
/// <see cref="InputSource"/>, поэтому такой стрип неотличим от обычного: у него
/// работают фader, mute, solo, индивидуальная посылка на шины и эффекты
/// (SM-B05). Отличается он ровно в двух вещах:
/// <list type="bullet">
/// <item>у него нет устройства, поэтому денойзер и ресемплер не нужны —
///     синтезатор уже отдаёт 48 кГц;</item>
/// <item>его кольцо пустое: весь звук приходит через очередь
///     <see cref="SampleQueue"/>, которую забирает <see cref="BusTap"/>.</item>
/// </list>
///
/// ПОЧЕМУ ЗВУК ИДЁТ ЧЕРЕЗ ОЧЕРЕДЬ, А НЕ В КОЛЬЦО.
///
/// Кольцо у такого стрипа забирает шина со скоростью выходного устройства, то
/// есть 48 кГц в секунду в секунду, и никакой второй полосы в нём не остаётся.
/// Синтезатор же отдаёт фразу целиком за доли секунды (измерено: 12,67 с звука за
/// 0,28 с). Положенная в кольцо как есть, она переполнила бы его мгновенно, и
/// читатель перескочил бы к последней секунде — на стриме это звучит как «озвучено
/// одно последнее слово, а начало сообщения проглочено».
///
/// Очередь снимает проблему: её забирает ровно тот тап, который и так уже
/// вызывается движком для этого стрипа, поэтому темп задаёт выходное устройство и
/// совпадает с остальным звуком стрипа по построению.
///
/// Эффекты стрипа применяются на приёме: у генерируемого стрипа нет потока
/// захвата, где они обычно считались бы, и пропустить их означало бы оставить
/// полосу без компрессора и реверберации, которые на живой полосе есть.
/// </summary>
public sealed class SyntheticInputSource : ISampleSource
{
    private const int Channels = InputSource.Channels;

    private readonly InputChannelModel _model;
    private readonly StripDsp _effects;
    private readonly SampleQueue _queue;

    public SyntheticInputSource(InputChannelModel model)
    {
        _model = model;
        _effects = new StripDsp(model);
        _queue = new SampleQueue(model.Name);
    }

    /// <summary>Очередь внешнего звука стрипа — её забирает тап.</summary>
    public SampleQueue External => _queue;

    /// <summary>Модель стрипа: тапу нужен она же, и она же решает судьбу звука.</summary>
    public InputChannelModel Model => _model;

    /// <summary>Есть ли что отдавать. Потока-задающего-темп больше нет, так что признак
    /// чисто диагностический: ненулевой остаток означает, что канал не слушают.</summary>
    public bool IsRunning => _queue.BufferedSeconds > 0;

    /// <summary>Сколько секунд подмешанного звука ещё не прозвучало.</summary>
    public double BufferedSeconds => _queue.BufferedSeconds;

    public RingCursor OpenCursor() => new SampleRingBuffer(1).OpenCursor();

    public void PushAudio(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        // Эффекты считаются здесь, потому что больше негде: потока захвата у
        // такого стрипа нет. Буфер копируется — очередь переживает вызов, а
        // обработка на месте оставила бы в SAPI буфер, который он перезапишет.
        int samples16 = samples.Length - (samples.Length % Channels);
        var packet = new float[samples16];
        samples[..samples16].CopyTo(packet);

        if (_model.IsMono) FoldToMono(packet, samples16 / Channels);
        ApplyGain(packet, samples16 / Channels);
        _effects.Process(packet, samples16 / Channels);

        _queue.Enqueue(packet);
    }

    /// <summary>Выбросить то, что ещё не ушло в шину (кнопка «стоп»).</summary>
    public bool ClearPending() => _queue.Clear();

    private static void FoldToMono(float[] buffer, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            float avg = (buffer[f * 2] + buffer[f * 2 + 1]) * 0.5f;
            buffer[f * 2] = avg;
            buffer[f * 2 + 1] = avg;
        }
    }

    /// <summary>
    /// Входное усиление стрипа. Рассуждение о нечисловых значениях — то же, что в
    /// <see cref="InputSource"/>: одно такое значение глушит канал целиком, потому
    /// что расходится по всем посылкам.
    /// </summary>
    private void ApplyGain(float[] buffer, int frames)
    {
        float gainDb = _model.GainDb;
        if (!float.IsFinite(gainDb) || gainDb == 0f) return;

        float gain = gainDb <= -60f ? 0f : (float)Math.Pow(10.0, gainDb / 20.0);
        int samples = frames * Channels;

        for (int i = 0; i < samples && i < buffer.Length; i++)
        {
            float v = buffer[i] * gain;
            buffer[i] = float.IsFinite(v) ? Math.Clamp(v, -4f, 4f) : 0f;
        }
    }

    public void Dispose()
    {
        // Старт эффектов «с нуля»: продолжение прерванного хвоста задержки или
        // реверберации дало бы щелчок в первом же пакете после перезапуска.
        _effects.Reset();
        _queue.Clear();
    }
}