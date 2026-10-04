using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Графический эквалайзер стрипа (SM-B05). Проверяется на сигнале и на кривой
/// АЧХ, а не на «метод не упал»: ошибка в EQ слышна как пропавший или вдвое
/// усиленный голос, и тест на отсутствие исключений её не поймает.
///
/// Вторая половина проверок — про совпадение кривой и сигнала. Рисунок в окне
/// настроек берёт АЧХ из той же формулы, что и DSP, поэтому расхождение между
/// «нарисовано» и «слышно» проверяется напрямую.
/// </summary>
public class EqualizerDspTests
{
    /// <summary>
    /// Полоса 2 кГц и её соседи. Индексы, а не частоты: полосы шага октавы, и
    /// соседние отличаются ровно вдвое — на них и строятся проверки изоляции.
    /// </summary>
    private const int MidBand = 6;

    private const float MidHz = 2000f;

    private static InputChannelModel Enabled(params (int Band, float Db)[] bands)
    {
        var model = new InputChannelModel { EqEnabled = true };
        foreach ((int band, float db) in bands) model.SetEqBand(band, db);
        return model;
    }

    /// <summary>
    /// Усиление на частоте в установившемся режиме: полоса подвинула синус на
    /// своё усиление, а на остальных частотах осталась как была.
    /// </summary>
    private static float MeasuredGain(StripDsp dsp, float hz, float amplitude = 0.2f)
    {
        float[] samples = Signal.Run(dsp, Signal.Sine(Signal.SampleRate, amplitude, hz));

        // Полсекунды на «разгон» фильтров и на сглаживание коэффициентов.
        return Signal.Peak(samples[(Signal.SampleRate / 2)..]) / amplitude;
    }

    [Fact]
    public void BoostRaisesTheBandFrequency()
    {
        var dsp = new StripDsp(Enabled((MidBand, 9f)));

        float gain = MeasuredGain(dsp, MidHz);

        Assert.InRange(gain, 2.5f, 3.2f);   // ≈ +9 дБ
    }

    [Fact]
    public void CutLowersTheBandFrequency()
    {
        var dsp = new StripDsp(Enabled((MidBand, -9f)));

        float gain = MeasuredGain(dsp, MidHz);

        Assert.InRange(gain, 0.25f, 0.4f);  // ≈ −9 дБ
    }

    [Fact]
    public void OneBandBarelyTouchesItsNeighbours()
    {
        var dsp = new StripDsp(Enabled((MidBand, 10f)));
        float peak = MeasuredGain(dsp, MidHz);

        // Ровно ноль здесь ждать нельзя: пики соседних полос всегда накрывают друг
        // друга, на этом и построен графический EQ — иначе подъём одной полосы
        // нельзя было бы отличить от подъёма всей середины. Проверяем поэтому
        // главное: на частоте самой полосы подъём есть, а через октаву в любую
        // сторону он уже неразличим на слух.
        Assert.InRange(peak, 2.8f, 3.4f);
        Assert.True(MeasuredGain(dsp, 1000f) * 2f < peak, "полоса 2 кГц тянет соседа 1 кГц");
        Assert.True(MeasuredGain(dsp, 4000f) * 2f < peak, "полоса 2 кГц тянет соседа 4 кГц");
    }

    [Fact]
    public void DisabledEqualizerIsBitTransparent()
    {
        var model = Enabled((5, 12f));
        model.EqEnabled = false;
        var dsp = new StripDsp(model);

        float[] dry = Signal.Sine(2048, 0.5f);
        float[] through = Signal.Run(dsp, dry);

        // Не «почти то же», а ровно то же: выключенный эффект не имеет права
        // трогать биты, иначе каждый стрип без EQ платит лишние 24 биквада на
        // каждый сэмпл.
        Assert.Equal(dry, through);
    }

    [Fact]
    public void LowCutRemovesTheBottom()
    {
        var model = Enabled();
        model.EqLowCutHz = 120f;
        var dsp = new StripDsp(model);

        Assert.True(MeasuredGain(dsp, 30f) < 0.08f);
        Assert.InRange(MeasuredGain(dsp, 2000f), 0.95f, 1.05f);
    }

    [Fact]
    public void HighCutRemovesTheTop()
    {
        var model = Enabled();
        model.EqHighCutHz = 3000f;
        var dsp = new StripDsp(model);

        Assert.True(MeasuredGain(dsp, 16000f) < 0.15f);
        Assert.InRange(MeasuredGain(dsp, 1000f), 0.95f, 1.05f);
    }

    [Fact]
    public void CutsAtTheEndsOfTheirScaleAreOff()
    {
        // Край шкалы = «не резать». ФНЧ на 20 кГц при 48 кГц всё равно снял бы
        // верхний октав, поэтому «выключено» обязано быть насквозь прозрачным.
        var dsp = new StripDsp(Enabled());

        Assert.InRange(MeasuredGain(dsp, 30f), 0.98f, 1.02f);
        Assert.InRange(MeasuredGain(dsp, 18000f), 0.98f, 1.02f);
    }

    [Fact]
    public void PreampShiftsTheWholeCurve()
    {
        var model = Enabled();
        model.EqPreampDb = -6f;
        var dsp = new StripDsp(model);

        Assert.InRange(MeasuredGain(dsp, 1000f), 0.48f, 0.53f);
    }

    [Fact]
    public void NaNAndOutOfRangeBandValuesNeverReachTheRingBuffer()
    {
        var model = Enabled((3, 5000f));
        model.SetEqBand(4, float.NaN);
        model.EqPreampDb = float.NaN;
        model.EqLowCutHz = float.NaN;
        model.EqHighCutHz = float.NaN;

        float[] through = Signal.Run(new StripDsp(model), Signal.Impulse(4800));

        Assert.All(through, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 4f));
    }

    [Fact]
    public void TheDrawnCurveIsWhatTheSignalDoes()
    {
        // Ровно то обещание окна настроек: кривая рисуется из
        // EqualizerResponse, и она обязана совпадать с тем, что делает DSP.
        var model = Enabled((0, 6f), (5, -8f), (9, 4f));
        model.EqLowCutHz = 80f;
        model.EqHighCutHz = 8000f;
        model.EqPreampDb = -3f;

        float[] freq = [50f, 250f, 1000f, 4000f, 12000f];
        var drawn = new float[freq.Length];
        var gains = new float[InputChannelModel.EqBandCount];
        for (int i = 0; i < gains.Length; i++) gains[i] = model.GetEqBand(i);

        EqualizerResponse.ComputeResponse(
            gains,
            model.EqPreampDb,
            model.EqLowCutHz,
            model.EqHighCutHz,
            InputSource.SampleRate,
            freq,
            drawn);

        var dsp = new StripDsp(model);
        foreach ((int i, float hz) in freq.Select((f, i) => (i, f)))
        {
            float measured = MeasuredGain(dsp, hz);
            float drawnDb = 20f * MathF.Log10(measured);

            Assert.InRange(drawnDb, drawn[i] - 1f, drawn[i] + 1f);
        }
    }

    [Fact]
    public void BandsAreOneOctaveApart()
    {
        // От геометрии полос зависит и ширина пиков, и подписи на кривой: полосы
        // с разным шагом налезали бы друг на друга иначе, чем рассчитана
        // добротность. «Октава» здесь — с точностью до стандартных центров
        // 31/62/125 Гц, поэтому не ровно, а с допуском.
        ReadOnlySpan<float> frequencies = InputChannelModel.EqBandFrequencies;

        Assert.Equal(InputChannelModel.EqBandCount, frequencies.Length);
        for (int i = 1; i < frequencies.Length; i++)
            Assert.InRange(frequencies[i] / frequencies[i - 1], 1.9f, 2.1f);
    }

    [Fact]
    public void EveryPresetHasBandsForEveryBandAndStaysInRange()
    {
        float limit = InputChannelModel.EqBandGainLimitDb;

        foreach (var preset in EqualizerPresets.All)
        {
            Assert.Equal(InputChannelModel.EqBandCount, preset.BandGainsDb.Length);

            foreach (float gain in preset.BandGainsDb)
                Assert.InRange(gain, -limit, limit);

            // Makeup-gain ограничен тем же пределом: пресет с подъёмом полос и
            // makeup в ноль — это способ уронить канал в кольцевой буфер, а он
            // обрезан по потолку уже после обработки.
            Assert.InRange(preset.PreampDb, -limit, limit);
        }
    }

    [Fact]
    public void PresetNamesAreTranslated()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            string name = Services.Loc.Get(preset.Key);
            Assert.NotEqual($"⟨{preset.Key}⟩", name);
        }
    }

    [Fact]
    public void ApplyingAPresetWritesBandsAndPreampAndKeepsTheCuts()
    {
        var model = new InputChannelModel { EqLowCutHz = 90f, EqHighCutHz = 12000f };
        var preset = EqualizerPresets.All[0];

        preset.ApplyTo(model);

        for (int i = 0; i < InputChannelModel.EqBandCount; i++)
            Assert.Equal(preset.BandGainsDb[i], model.GetEqBand(i), 2);

        Assert.Equal(preset.PreampDb, model.EqPreampDb, 2);

        // Срезы — настройка канала, а не тембра: пресет их не двигает.
        Assert.Equal(90f, model.EqLowCutHz);
        Assert.Equal(12000f, model.EqHighCutHz);
    }

    [Fact]
    public void APresetIsActuallyHeard()
    {
        // Пресет из одних нулей был бы пресетом по названию. Проверяем на сигнале:
        // голосовой пресет поднимает верхнюю середину и приглушает низ.
        var model = new InputChannelModel { EqEnabled = true };
        EqualizerPresets.All[0].ApplyTo(model);
        var dsp = new StripDsp(model);

        Assert.True(MeasuredGain(dsp, 1000f) > MeasuredGain(dsp, 100f));
        Assert.InRange(MeasuredGain(dsp, 100f), 0.4f, 0.9f);
    }
}