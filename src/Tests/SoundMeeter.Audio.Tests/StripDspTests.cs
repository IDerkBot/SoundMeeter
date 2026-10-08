using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Обработка стрипа (SM-B05): компрессор, trim, задержка, реверберация.
/// Проверяется на сигнале, а не на «метод не упал»: ошибка в DSP слышна как
/// щелчок или пропавший звук, и ни один тест на отсутствие исключений её не
/// поймает.
/// </summary>
public class StripDspTests
{
    [Fact]
    public void CompressorAttenuatesAboveTheThreshold()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            CompressorEnabled = true,
            CompressorThresholdDb = -20f,
            CompressorRatio = 4f,
            CompressorAttackMs = 5f,
            CompressorReleaseMs = 100f
        });

        float loudIn = Signal.Peak(Signal.Sine(Signal.SampleRate, 0.5f));   // ≈ −6 дБ
        float loudOut = Signal.Peak(Signal.Run(dsp, Signal.Sine(Signal.SampleRate, 0.5f)));

        Assert.True(loudOut < loudIn * 0.75f, $"{loudIn:F3} -> {loudOut:F3}");
    }

    [Fact]
    public void CompressorLeavesTheSignalBelowTheThresholdAlone()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            CompressorEnabled = true,
            CompressorThresholdDb = -20f,
            CompressorRatio = 4f
        });

        float quietIn = Signal.Peak(Signal.Sine(Signal.SampleRate, 0.05f));  // в‰€ в€’26 дБ
        float quietOut = Signal.Peak(Signal.Run(dsp, Signal.Sine(Signal.SampleRate, 0.05f)));

        Assert.True(MathF.Abs(quietOut - quietIn) < quietIn * 0.1f, $"{quietIn:F4} -> {quietOut:F4}");
    }

    [Fact]
    public void DisabledCompressorIsBitTransparent()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            CompressorEnabled = false,
            CompressorThresholdDb = -1f,
            CompressorRatio = 20f
        });

        float[] dry = Signal.Sine(2048, 0.5f);
        float[] through = Signal.Run(dsp, dry);

        // Не «почти то же», а ровно то же: выключенный эффект не имеет права
        // трогать биты, иначе каждый стрип без компрессора платит лишний
        // проход по буферу.
        Assert.Equal(dry, through);
    }

    [Fact]
    public void FxGainAppliesTheSetDecibels()
    {
        var dsp = new StripDsp(new InputChannelModel { FxGainEnabled = true, FxGainDb = -6f });

        float[] outSamples = Signal.Run(dsp, Signal.Sine(Signal.SampleRate, 0.5f));
        float settled = Signal.Peak(outSamples[(Signal.SampleRate)..]);

        Assert.True(MathF.Abs(settled / 0.5f - 0.501f) < 0.01f, settled.ToString("F4"));
    }

    [Fact]
    public void DisabledFxGainIsUnity()
    {
        var dsp = new StripDsp(new InputChannelModel { FxGainEnabled = false, FxGainDb = -20f });

        float[] outSamples = Signal.Run(dsp, Signal.Sine(1024, 0.5f));

        Assert.True(MathF.Abs(Signal.Peak(outSamples) - 0.5f) < 1e-4f);
    }

    [Fact]
    public void DelayEchoesAtTheSetTimeOnly()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            DelayEnabled = true,
            DelayTimeMs = 100f,
            DelayFeedback = 50f,
            DelayDampingHz = 18000f,
            DelayMix = 50f
        });

        float[] outSamples = Signal.Run(dsp, Signal.Impulse(Signal.SampleRate));

        Assert.True(MathF.Abs(outSamples[4800 * 2]) > 0.2f);   // ровно 100 мс
        Assert.True(MathF.Abs(outSamples[2400 * 2]) < 1e-6f);  // раньше 100 мс тишина
        Assert.True(MathF.Abs(outSamples[9600 * 2]) > 0.02f);  // повтор через обратную связь
    }

    [Fact]
    public void DisabledDelayIsDry()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            DelayEnabled = false,
            DelayTimeMs = 100f,
            DelayMix = 100f
        });

        float[] outSamples = Signal.Run(dsp, Signal.Impulse(4800));

        Assert.True(Signal.EnergyFrom(outSamples, 1) < 1e-9f);
    }

    [Fact]
    public void ReverbKeepsATailWithoutBlowingUp()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            ReverbEnabled = true,
            ReverbSize = 80f,
            ReverbDamping = 0.3f,
            ReverbMix = 70f
        });

        float[] outSamples = Signal.Run(dsp, Signal.Impulse(Signal.SampleRate));

        Assert.True(Signal.EnergyFrom(outSamples, 4800) > 1e-6f);
        Assert.True(Signal.Peak(outSamples) < 2f);
    }

    [Fact]
    public void DisabledReverbIsDry()
    {
        var dsp = new StripDsp(new InputChannelModel { ReverbEnabled = false, ReverbMix = 100f });

        float[] outSamples = Signal.Run(dsp, Signal.Impulse(4800));

        Assert.True(Signal.EnergyFrom(outSamples, 1) < 1e-9f);
    }

    [Fact]
    public void NaNAndInfinityNeverReachTheRingBuffer()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            ReverbEnabled = true,
            ReverbMix = 100f,
            DelayEnabled = true,
            DelayMix = 100f,
            DelayFeedback = 0.9f
        });

        float[] withNaN = Signal.Impulse(4800, 0, 1f);
        withNaN[10] = float.NaN;
        withNaN[11] = float.PositiveInfinity;

        float[] cleaned = Signal.Run(dsp, withNaN);

        // Один NaN в кольцевом буфере отравляет все последующие пакеты: стрип
        // замолчал бы навсегда, пока пользователь не перезапустит приложение.
        Assert.All(cleaned, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 4f));
    }

    [Fact]
    public void ResetSilencesTheReverbTail()
    {
        var dsp = new StripDsp(new InputChannelModel
        {
            ReverbEnabled = true,
            ReverbSize = 80f,
            ReverbDamping = 0.3f,
            ReverbMix = 70f
        });

        Signal.Run(dsp, Signal.Impulse(4800));
        dsp.Reset();
        float[] afterReset = Signal.Run(dsp, new float[4800 * 2]);

        Assert.True(Signal.EnergyFrom(afterReset, 0) < 1e-9f);
    }
}
