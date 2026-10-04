using System.Linq;
using SoundMeeter.Audio;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Кольцевой буфер между потоком WASAPI и потоком микшера. Ошибка здесь стоит
/// щелчка: буфер переполняется — теряются сэмплы, читатель отстаёт больше чем
/// на размер буфера — теряет данные без всякой ошибки, и обе потери слышны
/// только в наушниках на живом стрипе.
/// </summary>
/// <remarks>
/// Кольцо общее у всех выходов: один источник пишет, каждая шина читает своим
/// курсором. Поэтому здесь проверяется и «второй читатель не сдвинул первый».
/// </remarks>
public class SampleRingBufferTests
{
    [Fact]
    public void CursorReadsWhatWasWritten()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var cursor = ring.OpenCursor();
        ring.Write(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });

        var read = new float[4];

        Assert.Equal(4, cursor.Read(read, 4));
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, read);
    }

    [Fact]
    public void PartialFillIsNormalNotAnError()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var cursor = ring.OpenCursor();
        ring.Write(new float[] { 1f, 2f, 3f });

        var read = new float[10];

        // Источник отдаёт по 10 мс, микшер забирает по кругу: «всё или ничего»
        // здесь означало бы тишину в каждом несовпадении.
        Assert.Equal(3, cursor.Read(read, 10));
        Assert.Equal(new float[] { 1f, 2f, 3f }, read.Take(3));
    }

    [Fact]
    public void EmptyBufferReadsAsSilence()
    {
        var ring = new SampleRingBuffer(capacitySamples: 64);
        var cursor = ring.OpenCursor();

        Assert.Equal(0, cursor.Read(new float[16], 16));
    }

    [Fact]
    public void TwoCursorsAdvanceIndependently()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var first = ring.OpenCursor();
        var second = ring.OpenCursor();
        ring.Write(new float[] { 1f, 2f, 3f, 4f });

        var a = new float[2];
        var b = new float[4];

        Assert.Equal(2, first.Read(a, 2));

        // Второй выход отстал: он должен получить все четыре сэмпла, а не
        // продолжить с третьего — иначе шины рассыпаются по времени.
        Assert.Equal(4, second.Read(b, 4));
        Assert.Equal(new float[] { 1f, 2f, 3f, 4f }, b);
    }

    [Fact]
    public void AReaderThatFellBehindSkipsLostSamplesInsteadOfReadingGarbage()
    {
        var ring = new SampleRingBuffer(capacitySamples: 8);
        var cursor = ring.OpenCursor();

        // Пишем заведомо больше буфера, пока курсор ничего не читает.
        ring.Write(Enumerable.Range(0, 32).Select(i => (float)i).ToArray());

        var read = new float[4];

        Assert.Equal(4, cursor.Read(read, 4));

        // Старейшие 24 сэмпла физически затёрты. Читатель обязан получить хвост
        // буфера (24..27), а не начать с нуля — иначе на выходе будет щелчок
        // вместо звука.
        Assert.Equal(new float[] { 24f, 25f, 26f, 27f }, read);
    }

    [Fact]
    public void ZeroSizedPacketsDoNotBreakTheBuffer()
    {
        var ring = new SampleRingBuffer(capacitySamples: 64);
        var cursor = ring.OpenCursor();

        // Устройство может вернуть пустой пакет при остановке потока.
        ring.Write(Array.Empty<float>());

        Assert.Equal(64, ring.Capacity);
        Assert.Equal(0, cursor.Read(new float[8], 8));
    }

[Fact]
    public void WritesWrapAroundTheEndOfTheRing()
    {
        var ring = new SampleRingBuffer(capacitySamples: 4);
        var cursor = ring.OpenCursor();

        // Заведомо не кратно ёмкости: проверяет переход через конец массива.
        ring.Write(new float[] { 1f, 2f, 3f, 4f, 5f, 6f });

        var read = new float[4];

        Assert.Equal(4, cursor.Read(read, 4));
        Assert.Equal(new float[] { 3f, 4f, 5f, 6f }, read);
    }

    [Fact]
    public void BufferedDepthIsReportedInFrames()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var cursor = ring.OpenCursor();

        Assert.Equal(0, cursor.BufferedFrames);

        // 480 кадров стерео = 960 сэмплов.
        ring.Write(new float[960]);
        Assert.Equal(480, cursor.BufferedFrames);

        var read = new float[960];
        Assert.Equal(960, cursor.Read(read, 960));
        Assert.Equal(0, cursor.BufferedFrames);
    }

    [Fact]
    public void SkippedSamplesAreCountedWhenTheReaderFallsBehind()
    {
        var ring = new SampleRingBuffer(capacitySamples: 8);
        var cursor = ring.OpenCursor();

        Assert.Equal(0, cursor.SkippedFrames);

        // Ёмкость 8 сэмплов = 4 кадра стерео; записано 32 сэмпла = 16 кадров.
        // Затёрто 24 сэмпла, то есть потеряно ровно 12 кадров.
        ring.Write(Enumerable.Range(0, 32).Select(i => (float)i).ToArray());
        cursor.Read(new float[4], 4);

        Assert.Equal(12, cursor.SkippedFrames);
    }

    /// <summary>
    /// Ключевое свойство задержки: кольцо не умеет её копить.
    ///
    /// Раньше это ничем не проверялось, и именно поэтому непрочитанные сэмплы
    /// могли накапливаться: чем дальше читатель отставал, тем больше данных
    /// ждало в кольце и тем сильнее всё дальше отставало. Симптом у пользователя
    /// выглядел бы как «звук всё сильнее отстаёт», а не как щелчок.
    ///
    /// Здесь шина забирает ровно столько, сколько источник положил, — как и
    /// делает <c>WasapiPlayer</c>, — и очередь обязана остаться пустой.
    /// </summary>
    [Fact]
    public void ReadingAtTheRateOfWritingDoesNotBuildUpLatency()
    {
        var ring = new SampleRingBuffer(capacitySamples: 48000 * 2);   // 1 с
        var cursor = ring.OpenCursor();

        var packet = new float[480 * 2];
        var read = new float[480 * 2];
        int maxBuffered = 0;

        // 10 секунд по 10 мс: источник пишет пакет, шина тут же его забирает.
        for (int i = 0; i < 1000; i++)
        {
            packet.AsSpan().Fill(i % 7 - 3f);
            ring.Write(packet);
            Assert.Equal(960, cursor.Read(read, 960));
            maxBuffered = Math.Max(maxBuffered, cursor.BufferedFrames);
        }

        Assert.Equal(0, cursor.SkippedFrames);
        Assert.True(maxBuffered == 0,
            $"Очередь выросла до {maxBuffered} кадров: задержка копится, а не держится постоянной");
    }

    /// <summary>
    /// Обрыв источника не должен превращаться в тишину на кольцо: пока нет
    /// данных, курсор отдаёт 0, и вызывающий дополняет буфер тишиной — так и
    /// появляется разрыв в звуке при остановке приложения.
    /// </summary>
    [Fact]
    public void StarvedCursorReportsSilenceInsteadOfBlocking()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var cursor = ring.OpenCursor();

        var read = new float[64];

        Assert.Equal(0, cursor.Read(read, 64));
        Assert.Equal(0, cursor.SkippedFrames);

        ring.Write(new float[32]);
        Assert.Equal(32, cursor.Read(read, 64));
        Assert.Equal(0, cursor.Read(read, 64));
    }

    /// <summary>
    /// Новый курсор встаёт на живую границу кольца, а не на его начало.
    ///
    /// Регрессия: курсор начинал с позиции 0, то есть с <c>written − Capacity</c>
    /// — с целой секунды устаревшего звука. На живом микшере это читалось как
    /// «после остановки музыка играет ещё секунду», а в журнале — как кольцо на
    /// 430 мс при норме в 20 мс. Перезапуск движка или полосы лечил симптом
    /// (новое кольцо, нулевой счётчик), поэтому баг жил незаметно.
    /// </summary>
    [Fact]
    public void CursorOpenedOnAStaleRingStartsAtTheLiveEdgeNotInThePast()
    {
        // Ёмкость 4 кадра, пишем 10 секунд: кольцо давно переполнено и перезаписано.
        var ring = new SampleRingBuffer(capacitySamples: 8);
        var stale = Enumerable.Repeat(7f, 80_000).ToArray();
        ring.Write(stale);

        // Курсор создаётся ПОСЛЕ всего этого — как тап, поднятый на полосе,
        // которая молчала, пока кольцо перемалывало устаревший звук.
        var cursor = ring.OpenCursor();
        var read = new float[8];

        Assert.Equal(0, cursor.Read(read, 8));
        Assert.Equal(0, cursor.BufferedFrames);
    }

    /// <summary>
    /// То же для включения маршрута на работающем источнике: полоса уже пишет,
    /// кольцо уже переполнено, и новый тап обязан подхватить поток «здесь».
    /// </summary>
    [Fact]
    public void TapAddedMidStreamJoinsLiveInsteadOfReplayingASecondOfHistory()
    {
        var ring = new SampleRingBuffer(capacitySamples: 96_000);   // 1 с
        var live = new float[96_000];
        Array.Fill(live, 0.5f);

        for (int i = 0; i < 100; i++) ring.Write(live);   // 10 секунд в кольцо

        var late = ring.OpenCursor();
        var read = new float[4800];

        Assert.Equal(0, late.Read(read, 4800));
        Assert.Equal(0, late.SkippedFrames);
        Assert.Equal(0, late.BufferedFrames);

        // И дальше — в ногу с писателем, без накопления.
        for (int i = 0; i < 50; i++)
        {
            ring.Write(live);
            Assert.Equal(4800, late.Read(read, 4800));
        }
    }

    /// <summary>
    /// Два читателя на одном кольце, поднятые в разное время, — как две шины
    /// одной полосы. Поздний не должен догонять ранний и не должен сдвигать его:
    /// у каждого своя позиция, общее только кольцо.
    /// </summary>
    [Fact]
    public void ALateReaderDoesNotDisturbTheEarlyOne()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var early = ring.OpenCursor();

        ring.Write(Enumerable.Repeat(1f, 800).ToArray());
        Assert.Equal(400, early.Read(new float[400], 400));

        // Пока ранний «стоял», кольцо переползло: писатель ушёл на 1600, читатель
        // отстал на 400, затёрто всё, что между 400 и 576 (written − Capacity),
        // то есть 176 сэмплов = 88 кадров. Счётчик потерь растёт только на чтении.
        ring.Write(Enumerable.Repeat(2f, 800).ToArray());
        Assert.Equal(0, early.SkippedFrames);

        var late = ring.OpenCursor();
        var lateRead = new float[400];
        var earlyRead = new float[400];

        Assert.Equal(0, late.Read(lateRead, 400));
        Assert.Equal(400, early.Read(earlyRead, 400));
        Assert.Equal(88, early.SkippedFrames);

        // Ранний читает со своей позиции 576: там ещё единицы (позиции 576…799),
        // дальше — двойки (800…). Поздний при этом не сдвинул его ни на кадр.
        Assert.Equal(1f, earlyRead[0]);
        Assert.Equal(1f, earlyRead[223]);
        Assert.Equal(2f, earlyRead[224]);
        Assert.Equal(0, late.SkippedFrames);
    }
}
