using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Linq;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Ответвление стрипа в шину — единственное место, где сигнал из кольца
/// попадает в микшер. Ошибка здесь стоит либо щелчка, либо тишины на стрипе.
///
/// Отдельно проверяется длина чтения: она задаётся шириной буфера
/// <b>устройства</b>, а не приложения, и на некоторых драйверах это десятки
/// тысяч кадров. Тап обязан переваривать такой запрос, не путая блоки между
/// собой и не выделяя память на потоке рендера.
/// </summary>
/// <remarks>
/// Порядок во всех тестах — как в движке: сначала поднимается тап, потом
/// приходит звук. Обратный порядок (сначала наполнить кольцо, потом открыть
/// курсор) — это отдельный случай, и он разобран в
/// <see cref="SampleRingBufferTests"/>, потому что именно он и был багом.
/// </remarks>
public class BusTapTests
{
    private static float[] Run(BusTap tap, int samples)
    {
        var buffer = new float[samples];
        tap.Read(buffer);
        return buffer;
    }

    private static BusTap CreateTap(SampleRingBuffer ring, InputChannelModel? model = null,
        BusRouting? routing = null, int readBlockSize = 4096)
    {
        return new BusTap(ring.OpenCursor(), model ?? new InputChannelModel(),
            routing ?? new BusRouting(), new SoloState(), readBlockSize);
    }

    [Fact]
    public void PassesRingThroughUnchanged()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var tap = CreateTap(ring);

        var written = Enumerable.Range(0, 16).Select(i => i / 100f).ToArray();
        ring.Write(written);

        Assert.Equal(written, Run(tap, 16));
    }

    [Fact]
    public void StarvedTapReturnsSilenceAndKeepsItsPlaceInTheMixer()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var tap = CreateTap(ring);

        // Не 0, а запрошенное число: иначе MixingSampleProvider считает вход
        // «закончившимся» и молча выбрасывает его из микса навсегда.
        var buffer = new float[16];
        int read = tap.Read(buffer);

        Assert.Equal(16, read);
        Assert.All(buffer, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// Тап, поднятый на полосе, которая уже пишет, начинает с текущего края
    /// кольца, а не с его истории. Иначе он проигрывает секунду устаревшего
    /// звука — ровно тот симптом, из-за которого остановка воспроизведения
    /// «застревала» на лишнюю секунду.
    /// </summary>
    [Fact]
    public void TapRaisedOnAStripThatIsAlreadyWritingDoesNotReplayHistory()
    {
        var ring = new SampleRingBuffer(capacitySamples: 96_000);   // 1 с
        var history = new float[96_000];
        Array.Fill(history, 1f);

        for (int i = 0; i < 10; i++) ring.Write(history);   // кольцо переполнено

        // Полоса ожила: тап поднимают на уже работающем источнике.
        var tap = CreateTap(ring);

        Assert.All(Run(tap, 4096), v => Assert.Equal(0f, v));

        // И дальше звук идёт без задержки, начиная со следующего пакета.
        ring.Write(new float[4096]);
        Assert.All(Run(tap, 4096), v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// Запрос длиннее внутреннего блока. Раньше scratch разрастался под размер
    /// запроса; теперь он переиспользуется, а чтение идёт блоками — и блоки
    /// обязаны идти подряд, без сдвига и повтора, иначе сигнал «рассыпается» на
    /// стыках (слышно как тиканье на каждом переходе стереопары).
    /// </summary>
    [Theory]
    [InlineData(1024, 1025)]   // чуть больше блока
    [InlineData(512, 4097)]    // намного больше блока
    [InlineData(512, 5000)]    // не кратно размеру блока
    [InlineData(256, 100000)]  // ширина буфера «плохого» драйвера
    public void ReadLongerThanScratchStillReturnsEverySampleInOrder(int readBlockSize, int samples)
    {
        var ring = new SampleRingBuffer(capacitySamples: samples * 2 + 16);
        var tap = CreateTap(ring, readBlockSize: readBlockSize);

        var written = Enumerable.Range(0, samples).Select(i => (i % 2048) / 4096f - 0.25f).ToArray();
        ring.Write(written);

        var buffer = new float[samples];
        Assert.Equal(samples, tap.Read(buffer));
        Assert.Equal(written, buffer);
    }

    /// <summary>
    /// Скорость чтения не должна влиять на содержимое: блок больше выборки и
    /// блок меньше выборки дают один и тот же результат.
    /// </summary>
    [Fact]
    public void ChunkingDoesNotChangeTheSignal()
    {
        var samples = 3000;
        var written = Enumerable.Range(0, samples).Select(i => MathF.Sin(i / 8f)).ToArray();

        float[] Read(int block)
        {
            var ring = new SampleRingBuffer(capacitySamples: samples * 2 + 16);
            var tap = CreateTap(ring, readBlockSize: block);
            ring.Write(written);
            return Run(tap, samples);
        }

        Assert.Equal(Read(4096), Read(256));
    }

    [Fact]
    public void VolumeAndSendAreApplied()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var model = new InputChannelModel { VolumeDb = -6f };
        var tap = CreateTap(ring, model, new BusRouting { GainDb = -6f });

        ring.Write(new float[] { 1f, 1f });

        // -6 дБ и ещё -6 дБ ≈ 0.25. Сравниваем с допуском: дБ → линейный →
        // обратно не даёт ровно 0.25, и требовать точное равенство означало бы
        // тестировать float, а не посылку.
        Assert.All(Run(tap, 2), v => Assert.Equal(0.2512f, v, 3));
    }

    [Fact]
    public void MuteSilencesTheTapButKeepsReturningSamples()
    {
        var ring = new SampleRingBuffer(capacitySamples: 1024);
        var tap = CreateTap(ring, new InputChannelModel { IsMuted = true });

        ring.Write(new float[] { 1f, 1f });

        var buffer = new float[2];
        Assert.Equal(2, tap.Read(buffer));
        Assert.Equal(new float[] { 0f, 0f }, buffer);
    }
}