using System.Diagnostics;
using SoundMeeter.Audio;
using SoundMeeter.Models;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Источник стрипа, наполняемый кодом (SM-E01).
///
/// Темп выдачи проверяется здесь не по времени, а по количеству прочитанного:
/// источник ничего не «крутит» сам, читателем является тап, и тап забирает ровно
/// столько, сколько у него попросили. Поэтому проверять надо одно: очередь
/// отдаёт данные, и отдаёт их в исходном виде, а не огрызком. Тот факт, что
/// фраза не может вычеркнуться мгновенно, обеспечивается на уровне шины —
/// она читает блоками по 10 мс, и ровно столько же забирает тап.
/// </summary>
public class SyntheticInputSourceTests
{
    private const int SampleRate = InputSource.SampleRate;
    private const int Channels = InputSource.Channels;

    /// <summary>Один блок шины: 10 мс.</summary>
    private const int BlockFrames = SampleRate / 100;

    /// <summary>
    /// Тап, подключённый к источнику. Создаётся ДО отправки звука — очередь не
    /// переигрывает новому читателю накопленное, он встаёт на живую границу, как и
    /// RingCursor. Настоящая шина существует всегда, а тест обязан это отражать,
    /// иначе проверял бы не работу источника, а порядок строк в тесте.
    /// </summary>
    private sealed class Bus
    {
        private readonly BusTap _tap;

        public Bus(SyntheticInputSource source, SampleRingBuffer? ring = null)
        {
            _tap = new BusTap(
                (ring ?? new SampleRingBuffer(SampleRate * Channels)).OpenCursor(),
                source.Model,
                new BusRouting(),
                new SoloState(),
                extra: source.External);
        }

        /// <summary>Забирает ровно столько кадров, сколько попросили, — тем же путём,
        /// что и настоящая шина.</summary>
        public float[] Drain(int framesWanted)
        {
            var collected = new List<float>(framesWanted * Channels);
            var block = new float[BlockFrames * Channels];

            while (collected.Count < framesWanted * Channels)
            {
                int read = _tap.Read(block.AsSpan(0, block.Length));
                for (int i = 0; i < read; i++) collected.Add(block[i]);
            }

            return collected.ToArray();
        }
    }

    [Fact]
    public void NothingIsWrittenUntilDataArrives()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);

        Assert.Equal(0, source.BufferedSeconds);
        Assert.False(source.IsRunning);
    }

    /// <summary>
    /// Главное свойство: речь уходит НЕ огрызком. Синтезатор отдаёт фразу целиком за
    /// доли секунды, и раньше она ложилась прямо в кольцо источника, где тот же
    /// переполнялся мгновенно. Теперь данные лежат в очереди, и уходят в темпе
    /// шины — то есть полностью.
    /// </summary>
    [Fact]
    public void TheWholePhraseIsDeliveredNotATail()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);

        var bus = new Bus(source);

        // Полсекунды речи одной порцией — как это делает синтезатор.
        source.PushAudio(Fill(BlockFrames * 50 * Channels, 0.5f));

        var read = bus.Drain(BlockFrames * 50);

        Assert.Equal(BlockFrames * 50 * Channels, read.Length);
        Assert.All(read, v => Assert.Equal(0.5f, v, 4));
    }

    /// <summary>
    /// Сколько секунд осталось ждать — это то, чем модуль речи пользуется, чтобы
    /// понять, что фраза доиграна. Раньше для стрипа с устройством эта величина
    /// всегда была нулём, и ожидание конца фразы вырождалось.
    /// </summary>
    [Fact]
    public void TheRemainingTimeIsVisibleToTheSpeechModule()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);
        var bus = new Bus(source);

        source.PushAudio(Fill(BlockFrames * 100 * Channels, 0.5f));   // 1 с
        Assert.Equal(1.0, source.BufferedSeconds, 2);

        bus.Drain(BlockFrames * 50);
        Assert.Equal(0.5, source.BufferedSeconds, 2);
    }

    /// <summary>
    /// Кольцо общее у всех выходов, поэтому два тапа на одном источнике видят ОДНУ и
    /// ту же речь целиком. Это и отличает генерируемый источник от «синтеза по запросу
    /// на каждого читателя»: во втором случае фраза разъехалась бы между шинами, и
    /// одна из них зазвучала бы со второй половиной слов.
    /// </summary>
    [Fact]
    public void TwoBusesReceiveTheSameSpeech()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);
        var firstBus = new Bus(source);
        var secondBus = new Bus(source);

        source.PushAudio(Fill(BlockFrames * 10 * Channels, 0.25f));

        var first = firstBus.Drain(BlockFrames * 10);
        var second = secondBus.Drain(BlockFrames * 10);

        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Fader стрипа применяется к подмешанному сигналу. Проверяется на −6 дБ: стрип,
    /// созданный модулем, должен вести себя как любой другой — иначе его нельзя
    /// было бы приглушить, не выключая модуль.
    /// </summary>
    [Fact]
    public void StripGainIsAppliedToGeneratedAudio()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true, GainDb = -6f };
        using var source = new SyntheticInputSource(model);
        var bus = new Bus(source);

        source.PushAudio(Fill(BlockFrames * 10 * Channels, 0.5f));

        var read = bus.Drain(BlockFrames * 10);

        Assert.NotEmpty(read);
        // −6 дБ — ровно половина амплитуды. Допуск покрывает округление float.
        Assert.All(read, v => Assert.InRange(v, 0.24f, 0.26f));
    }

    /// <summary>
    /// Сигнал должен быть конечным: одно NaN расходится по всем посылкам и глушит
    /// канал целиком (то же рассуждение, что в InputSource и StripDsp).
    /// </summary>
    [Fact]
    public void NonFiniteSamplesNeverReachTheBus()
    {
        var model = new InputChannelModel
        {
            Name = "TTS",
            IsGenerated = true,
            FxGainEnabled = true,
            FxGainDb = 40f,
        };
        using var source = new SyntheticInputSource(model);
        var bus = new Bus(source);

        var dirty = new float[BlockFrames * 10 * Channels];
        dirty[0] = float.NaN;
        dirty[^1] = float.PositiveInfinity;
        for (int i = 2; i < dirty.Length - 2; i++) dirty[i] = 0.3f;

        source.PushAudio(dirty);

        var read = bus.Drain(BlockFrames * 10);

        Assert.NotEmpty(read);
        Assert.All(read, v => Assert.True(float.IsFinite(v), $"В шину попало нечисловое значение {v}"));
    }

    [Fact]
    public void PushAfterDisposeIsIgnoredInsteadOfThrowing()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        var source = new SyntheticInputSource(model);

        source.PushAudio(Fill(1024, 0.1f));
        source.Dispose();

        // Освобождение источника может произойти на любом потоке (перебор
        // маршрутов движком), и синтезатор в этот момент вполне может писать.
        source.PushAudio(Fill(1024, 0.1f));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        var source = new SyntheticInputSource(model);

        source.Dispose();
        source.Dispose();
    }

    /// <summary>
    /// Пока очередь не читают, копить речь незачем: полоса может быть перекрыта
    /// (микрофон в монополии, кабель не слушают), и без потолка это просто рост
    /// памяти. При появлении читателя очередь заполняется снова.
    /// </summary>
    [Fact]
    public void NothingIsKeptWhileNobodyReads()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);

        var chunk = Fill(BlockFrames * Channels, 0.1f);
        for (int i = 0; i < 20 * 60; i++) source.PushAudio(chunk);   // 20 минут речи

        Assert.Equal(0, source.BufferedSeconds);
        Assert.Equal(0, source.BufferedSeconds);
    }

    /// <summary>
    /// «Стоп» должен глушить то, что синтезатор уже успел отдать. Без этого фраза
    /// доигрывает сама: SAPI отдал её целиком за доли секунды, и к моменту нажатия
    /// она целиком лежит в очереди.
    /// </summary>
    [Fact]
    public void WhatHasNotBeenHeardYetCanBeDropped()
    {
        var model = new InputChannelModel { Name = "TTS", IsGenerated = true };
        using var source = new SyntheticInputSource(model);

        Assert.False(source.ClearPending());   // пустая очередь — ничего не выброшено

        _ = new Bus(source);
        source.PushAudio(Fill(BlockFrames * 50 * Channels, 0.5f));
        Assert.True(source.BufferedSeconds > 0);

        Assert.True(source.ClearPending());
        Assert.Equal(0, source.BufferedSeconds);
        Assert.False(source.ClearPending());   // повторное нажатие «стоп» безвредно
    }

    private static float[] Fill(int samples, float value)
    {
        var buffer = new float[samples];
        Array.Fill(buffer, value);
        return buffer;
    }
}