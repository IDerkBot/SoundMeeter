using System.Text;
using SoundMeeter.Services.TextToSpeech;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Конвертация PCM16/моно → float/стерео 48 кГц (SM-E01).
///
/// Это единственное место в модуле с нетривиальной арифметикой, и ошибка в нём
/// слышна только в эфире: речь либо замедляется вдвое, либо заикается на каждой
/// границе порции. Проверяется без SAPI, без звуковой карты и без голосов в
/// системе — чистая математика на байтах.
/// </summary>
public class Pcm16MonoStreamTests
{
    private const int TargetRate = Pcm16MonoStream.TargetRate;
    private const int ChunkFrames = Pcm16MonoStream.ChunkFrames;

    private static byte[] Pcm16(params short[] samples)
    {
        var buffer = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            buffer[i * 2] = (byte)(samples[i] & 0xFF);
            buffer[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return buffer;
    }

    /// <summary>
    /// Растущий сигнал во всём диапазоне int16. Растущий — потому что на нём
    /// видно пропуск или повтор отсчёта: проверка монотонности ловит ошибку
    /// смещения, которую сравнение «примерно равно» пропустило бы.
    /// </summary>
    private static byte[] Ramp(int samples)
    {
        var buffer = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short value = (short)((int)i * short.MaxValue / Math.Max(1, samples - 1));
            buffer[i * 2] = (byte)(value & 0xFF);
            buffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return buffer;
    }

    /// <summary>
    /// При равных частотах отсчёты должны попадать в выход один в один: это
    /// предпочтительный случай, когда SAPI отдаёт 48 кГц, и ресемплинга быть не
    /// должно вовсе.
    /// </summary>
    [Fact]
    public void AtTheSameRateSamplesAreCopiedOneToOne()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => collected.AddRange(chunk));

        stream.Write(Pcm16(0, 1000, -1000, 32767, -32768));
        stream.Complete();

        // Пять входных отсчётов дают четыре выходных: у последнего нет правого
        // соседа для интерполяции, и он не выдумывается.
        Assert.Equal(4, collected.Count / 2);
        Assert.Equal(0f, collected[0], 4);
        Assert.Equal(1000f / 32768f, collected[2], 4);
        Assert.Equal(-1000f / 32768f, collected[4], 4);
        Assert.Equal(1f, collected[6], 4);
    }

    /// <summary>Моно раскладывается в оба канала: речь в микшере идёт по центру.</summary>
    [Fact]
    public void MonoIsLaidIntoBothChannels()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => collected.AddRange(chunk));

        stream.Write(Pcm16(8000, 9000));
        stream.Complete();

        // Один выходной кадр — два одинаковых значения: пара сэмплов.
        Assert.Equal(new[] { 8000f / 32768f, 8000f / 32768f }, collected);
    }

    /// <summary>
    /// При 24 кГц на каждый входной отсчёт приходится ровно два выходных, и
    /// нечётные выходные лежат РОВНО посередине между соседями. Проверяется и
    /// количество кадров, и сами значения: рассинхрон на один отсчёт здесь
    /// слышен как «речь то спешит, то опаздывает».
    /// </summary>
    [Fact]
    public void DoublingTheRatePutsEverySecondSampleExactlyOnTheInput()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate / 2, chunk => collected.AddRange(chunk));

        stream.Write(Pcm16(0, 2000, 6000, 12000, 20000, 30000));
        stream.Complete();

        // Шесть входных отсчётов дают десять выходных: каждому нужен правый сосед
        // для интерполяции, а у последнего его уже нет.
        var frames = collected.Count / 2;
        Assert.Equal(10, frames);

        float Step(int index) => collected[index * 2];
        Assert.Equal(0f, Step(0), 4);
        Assert.Equal(1000f / 32768f, Step(1), 4);           // середина между 0 и 2000
        Assert.Equal(2000f / 32768f, Step(2), 4);           // точно во входной
        Assert.Equal(4000f / 32768f, Step(3), 4);
        Assert.Equal(6000f / 32768f, Step(4), 4);
        Assert.Equal(9000f / 32768f, Step(5), 4);
        Assert.Equal(12000f / 32768f, Step(6), 4);
        Assert.Equal(16000f / 32768f, Step(7), 4);
        Assert.Equal(20000f / 32768f, Step(8), 4);
        Assert.Equal(25000f / 32768f, Step(9), 4);    }

    /// <summary>
    /// Стык порций не должен терять и не должен дублировать отсчёты. SAPI режет
    /// поток как удобно ему, поэтому порция может прийтись на нечётный байт и на
    /// середину интерполяции.
    /// </summary>
    [Fact]
    public void SplittingTheInputAcrossWritesDoesNotChangeTheResult()
    {
        var whole = new List<float>();
        var split = new List<float>();

        var one = new Pcm16MonoStream(TargetRate, chunk => whole.AddRange(chunk));
        one.Write(Ramp(3000));
        one.Complete();

        // Тот же сигнал, но тремя невыровненными кусками — второй начинается с
        // нечётного байта.
        var three = new Pcm16MonoStream(TargetRate, chunk => split.AddRange(chunk));
        var bytes = Ramp(3000);
        three.Write(bytes.AsSpan(0, 4001));
        three.Write(bytes.AsSpan(4001, 1000));
        three.Write(bytes.AsSpan(5001, 999));
        three.Complete();

        Assert.Equal(whole, split);
    }

    /// <summary>
    /// Длительность не «уплывает» на длинном сигнале, разбитом на много порций.
    /// Секунда входных данных при 24 кГц обязана дать ровно две секунды на 48 кГц;
    /// ошибка в одном отсчёте на стыке накапливалась бы и через минуту речь
    /// заметно уехала бы по времени.
    /// </summary>
    [Fact]
    public void InterpolationStaysAccurateAcrossManyPortions()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate / 2, chunk => collected.AddRange(chunk));

        // Секунда моно при 24 кГц, приходящая кусками по три кадра — много стыков.
        var bytes = Ramp(TargetRate / 2);
        int piece = ChunkFrames * 2 * 3;
        for (int offset = 0; offset < bytes.Length; offset += piece)
        {
            stream.Write(bytes.AsSpan(offset, Math.Min(piece, bytes.Length - offset)));
        }
        stream.Complete();

        var frames = collected.Count / 2;

        // Ровно два кадра на входной минус один: последнему не хватает соседа.
        Assert.Equal(TargetRate - 2, frames);

        // Сигнал остаётся монотонным и покрывает весь входной диапазон — значит,
        // интерполяция не заворачивается и не теряет отсчёты.
        for (int i = 1; i < frames; i++)
        {
            Assert.True(collected[i * 2] >= collected[(i - 1) * 2],
                $"Отсчёт {i} ушёл назад: {collected[(i - 1) * 2]} → {collected[i * 2]}");
        }

        Assert.Equal(0f, collected[0], 4);
        Assert.Equal(collected[(frames - 1) * 2], collected[frames * 2 - 1], 5);
    }

    [Fact]
    public void PortionsAreDeliveredInChunksNotSamples()
    {
        var chunks = new List<int>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => chunks.Add(chunk.Length / 2));

        stream.Write(Pcm16(Enumerable.Range(0, 2000).Select(i => (short)(i * 10)).ToArray()));
        stream.Complete();

        Assert.NotEmpty(chunks);
        Assert.All(chunks.Take(chunks.Count - 1), frames => Assert.Equal(ChunkFrames, frames));
    }

    [Fact]
    public void TheTailOfAPortionIsNotHeldBackUntilTheNextWrite()
    {
        var chunks = new List<int>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => chunks.Add(chunk.Length / 2));

        // Ровно одна порция и ещё 100 отсчётов.
        stream.Write(Pcm16(Enumerable.Range(0, ChunkFrames + 100).Select(i => (short)(i * 10)).ToArray()));

        // Хвост уходит вместе с текущей записью, а не копится до следующей.
        // SAPI пишет в поток раз в 100–200 мс, и ожидание полной порции добавляло
        // бы к началу каждой фразы столько же тишины.
        Assert.Equal(new[] { ChunkFrames, 99 }, chunks);

        stream.Complete();
        Assert.Equal(2, chunks.Count);   // пустой хвост ничего не отдаёт
    }

    [Fact]
    public void AnOddTrailingByteDoesNotBreakTheStream()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => collected.AddRange(chunk));

        // SAPI вправе завершить порцию нечётным числом байт.
        stream.Write(Pcm16(1000, 2000, 3000).Concat(new byte[] { 0x34 }).ToArray());
        stream.Write(Pcm16(4000));
        stream.Complete();

        Assert.NotEmpty(collected);
        Assert.All(collected, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void EmptyWritesAreHarmless()
    {
        var collected = new List<float>();
        var stream = new Pcm16MonoStream(TargetRate, chunk => collected.AddRange(chunk));

        stream.Write(ReadOnlySpan<byte>.Empty);
        stream.Complete();

        Assert.Empty(collected);
    }

    /// <summary>
    /// SAPI при подключении потока вывода читает <c>Position</c> и <c>Length</c>, и
    /// исключение оттуда прерывает синтез целиком с сообщением «SAPI не поддерживает
    /// формат» — хотя поддерживает. Регрессия поймана сквозной проверкой
    /// <see cref="TextToSpeechIntegrationTests"/>.
    /// </summary>
    [Fact]
    public void TheStreamReportsItsPositionInsteadOfRefusing()
    {
        var stream = new Pcm16MonoStream(TargetRate, _ => { });

        Assert.Equal(0, stream.Position);
        Assert.Equal(0, stream.Length);

        stream.Write(Pcm16(1, 2, 3, 4));

        Assert.Equal(8, stream.Position);
        Assert.Equal(8, stream.Length);

        // Позиция только счётчик: SAPI её читает, но назад по потоку не ходит.
        stream.Position = 0;
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void TheStreamRefusesToBeReadOrSeeked()
    {
        var stream = new Pcm16MonoStream(TargetRate, _ => { });

        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void AnImpossibleSourceRateIsRejectedRightAway()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pcm16MonoStream(0, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new Pcm16MonoStream(48000, null!));
    }
}
