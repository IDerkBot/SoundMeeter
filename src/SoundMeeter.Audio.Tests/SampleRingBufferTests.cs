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
}
