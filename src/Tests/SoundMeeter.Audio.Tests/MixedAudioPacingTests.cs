using SoundMeeter.Audio;
using SoundMeeter.Models;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Подмешивание синтезированной речи в стрип, у которого УЖЕ ЕСТЬ поток захвата
/// устройства (SM-E01) — то есть кабельный стрип.
///
/// Это отдельный случай, и он ломался отдельно от выделенного канала TTS. На
/// выделенном стрипе кольцо наполняет только модуль, и всё выглядит правильно. На
/// кабельном кольцо уже занято: loopback-захват кладёт в него ровно столько,
/// сколько шина забирает, — 48 кГц в секунду в секунду. Свободной полосы нет, и
/// положенная туда напрямую речь (синтезатор отдаёт фразу за доли секунды)
/// переполняла кольцо мгновенно: читатель перескакивал к последней секунде, и на
/// стриме звучало одно последнее слово.
///
/// Здесь моделируется ровно это: кольцо заполняется «устройством» и забирается
/// «шиной» с той же скоростью, а речь приходит отдельной очередью и складывается
/// тапом на выходе.
/// </summary>
public class MixedAudioPacingTests
{
    private const int SampleRate = InputSource.SampleRate;
    private const int Channels = InputSource.Channels;
    private const int BlockFrames = SampleRate / 100;

    private const float SpeechLevel = 0.5f;
    private const float DeviceLevel = 0.1f;

    /// <summary>
    /// Речь услышана целиком и в правильном темпе, несмотря на занятое кольцо.
    /// Раньше оставался только последний конец фразы.
    /// </summary>
    [Fact]
    public void SpeechIsMixedIntoADeviceBackedStripInFull()
    {
        var model = new InputChannelModel { Name = "CABLE-A Input", VolumeDb = 0f };
        var ring = new SampleRingBuffer(SampleRate * Channels);
        var queue = new SampleQueue(model.Name);

        var tap = new BusTap(ring.OpenCursor(), model, new BusRouting(), new SoloState(),
            readBlockSize: BlockFrames * Channels, extra: queue);
        var outBuffer = new float[BlockFrames * Channels];

        // Читатель создаётся ДО Enqueue: очередь не переигрывает накопившееся новому
        // читателю, поэтому подключённый посреди фразы тап услышит её остаток, а не
        // начало заново. Так же ведёт себя и RingCursor.
        var reader = queue.OpenReader();

        // Речь 4 с, отдаётся вся сразу — как это делает SAPI (заметно быстрее
        // реального времени), и при этом кольцо уже занято устройством.
        var speech = new float[SampleRate * 4 * Channels];
        for (int f = 0; f < SampleRate * 4; f++) speech[f * 2] = speech[f * 2 + 1] = SpeechLevel;
        queue.Enqueue(speech);

        int speechHeard = 0;

        // Гоняем 5 с шины (больше, чем 4 с речи) блоками по 10 мс.
        for (int block = 0; block < 500; block++)
        {
            // «Устройство» пишет в кольцо, «шина» забирает столько же.
            var device = new float[BlockFrames * Channels];
            Array.Fill(device, DeviceLevel);
            ring.Write(device);

            int read = tap.Read(outBuffer.AsSpan(0, outBuffer.Length));
            for (int i = 0; i < read; i++)
            {
                float v = Math.Abs(outBuffer[i]);
                if (Math.Abs(v - (SpeechLevel + DeviceLevel)) < 0.05f) speechHeard++;
            }
        }

        double heardSpeechSeconds = speechHeard / (double)(SampleRate * Channels);
        Assert.InRange(heardSpeechSeconds, 3.9, 4.2);
    }

    /// <summary>
    /// Кольцо занято, и это не мешает: речь смешивается с ним, а не вытесняет.
    /// Обе компоненты должны быть слышны одновременно.
    /// </summary>
    [Fact]
    public void SpeechIsSummedWithTheDeviceSignalRatherThanReplacingIt()
    {
        var model = new InputChannelModel { Name = "CABLE-A Input" };
        var ring = new SampleRingBuffer(SampleRate * Channels);
        var queue = new SampleQueue(model.Name);

        var tap = new BusTap(ring.OpenCursor(), model, new BusRouting(), new SoloState(), extra: queue);
        var buffer = new float[BlockFrames * Channels];

        // Кольцо заполнено на весь запрашиваемый блок, иначе половина суммы была бы
        // «речь + тишина» и сравнение с суммой не имело бы смысла.
        var device = new float[BlockFrames * Channels];
        Array.Fill(device, DeviceLevel);
        ring.Write(device);

        var speech = new float[BlockFrames * Channels];
        for (int i = 0; i < speech.Length; i++) speech[i] = SpeechLevel;
        queue.Enqueue(speech);

        int read = tap.Read(buffer.AsSpan(0, buffer.Length));

        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, v => Assert.Equal(SpeechLevel + DeviceLevel, v, 3));
    }

    /// <summary>Остаток виден модулю речи: без него ожидание конца фразы вырождается в ноль.</summary>
    [Fact]
    public void TheQueueReportsWhatHasNotBeenHeardYet()
    {
        var queue = new SampleQueue("CABLE-A Input");
        var reader = queue.OpenReader();
        Assert.Equal(0, queue.BufferedSeconds);

        queue.Enqueue(new float[SampleRate * Channels]);        // 1 с
        Assert.Equal(1.0, queue.BufferedSeconds, 2);

        reader.Read(new float[SampleRate / 2 * Channels], SampleRate / 2 * Channels);
        Assert.Equal(0.5, queue.BufferedSeconds, 2);
    }

    /// <summary>Очередь не копится бесконечно, если канал никто не слушает.</summary>
    [Fact]
    public void TheQueueIsBoundedWhenNobodyReadsIt()
    {
        var queue = new SampleQueue("CABLE-A Input");
        queue.OpenReader();
        queue.OpenReader();

        var chunk = new float[SampleRate * Channels];
        for (int i = 0; i < 20 * 60; i++) queue.Enqueue(chunk);   // 20 минут

        Assert.InRange(queue.BufferedSeconds, 0.5, 30.5);
    }

    [Fact]
    public void ClearingDropsOnlyWhatWasNotYetHeard()
    {
        var queue = new SampleQueue("CABLE-A Input");
        queue.OpenReader();

        Assert.False(queue.Clear());

        queue.Enqueue(new float[SampleRate * Channels]);
        Assert.True(queue.Clear());
        Assert.Equal(0, queue.BufferedSeconds);
    }
}