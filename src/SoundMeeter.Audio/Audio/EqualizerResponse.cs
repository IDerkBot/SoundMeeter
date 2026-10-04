using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Коэффициенты фильтров эквалайзера и его амплитудная характеристика — одно
/// место, где живёт математика. DSP считает по ней сигнал, а окно настроек по
/// той же формуле рисует кривую: если бы формулы расходились, пользователь тянул
/// бы полосу и не получал того, что нарисовано.
///
/// Цепочка фильтров всегда одной длины и одного порядка:
/// <c>срез снизу → полосы (31 Гц…16 кГц) → срез сверху</c>. Общий makeup-gain —
/// не фильтр, а линейное усиление, поэтому в цепочку не входит и в АЧХ входит
/// как постоянная добавка. </summary>
public static class EqualizerResponse
{
    /// <summary>Фильтров в цепочке: два среза плюс все полосы.</summary>
    public const int FilterCount = InputChannelModel.EqBandCount + 2;

    /// <summary>Индекс фильтра среза снизу.</summary>
    public const int LowCutIndex = 0;

    /// <summary>Индекс фильтра среза сверху.</summary>
    public const int HighCutIndex = FilterCount - 1;

    /// <summary>
    /// Добротность полосы. По формуле RBJ ширина пика ≈ 1.019·f0/Q октав, то
    /// есть при Q = 1.4 полоса — примерно 0.7 октавы: соседние пики (шаг октавы)
    /// почти не накрывают друг друга, и полоса звучит как своя, а не как часть
    /// соседней.
    /// </summary>
    public const float BandQ = 1.4f;

    /// <summary>Добротность срезов: 0.707 — Баттерворт, 12 дБ/окт.</summary>
    private const float CutQ = 0.70710678f;

    /// <summary>
    /// Посчитать коэффициенты цепочки. <paramref name="filters"/> должен быть
    /// длиной <see cref="FilterCount"/>; полос берётся не больше, чем есть в
    /// <paramref name="bandGainsDb"/>, — недостающие считаются ровными.
    ///
    /// Состояние фильтров не трогается (см. <see cref="BiquadFilter"/>): вызов
    /// идёт из обработки, где фильтры уже звучат.
    /// </summary>
    public static void Compute(
        ReadOnlySpan<float> bandGainsDb,
        float lowCutHz,
        float highCutHz,
        float sampleRate,
        Span<BiquadFilter> filters)
    {
        // Крайние положения срезов — это «не резать», а не «резать на 20 Гц» и
        // «резать на 20 кГц»: срез, поставленный на границу шкалы, всё равно
        // слегка правит сигнал, а пользователь ждёт от него полной прозрачности.
        if (lowCutHz <= InputChannelModel.EqLowCutMinHz) filters[LowCutIndex].SetPassThrough();
        else filters[LowCutIndex].SetHighPass(lowCutHz, CutQ, sampleRate);

        int bands = Math.Min(InputChannelModel.EqBandCount, bandGainsDb.Length);
        ReadOnlySpan<float> frequencies = InputChannelModel.EqBandFrequencies;
        for (int i = 0; i < bands; i++)
            filters[LowCutIndex + 1 + i].SetPeaking(frequencies[i], bandGainsDb[i], BandQ, sampleRate);

        if (highCutHz >= InputChannelModel.EqHighCutMaxHz) filters[HighCutIndex].SetPassThrough();
        else filters[HighCutIndex].SetLowPass(highCutHz, CutQ, sampleRate);
    }

    /// <summary>
    /// Суммарная АЧХ эквалайзера на заданных частотах, дБ. Ровно то, что слышно:
    /// окно настроек рисует только эту функцию.
    /// </summary>
    public static void ComputeResponse(
        ReadOnlySpan<float> bandGainsDb,
        float preampDb,
        float lowCutHz,
        float highCutHz,
        float sampleRate,
        ReadOnlySpan<float> freqHz,
        Span<float> responseDb)
    {
        Span<BiquadFilter> filters = stackalloc BiquadFilter[FilterCount];
        Compute(bandGainsDb, lowCutHz, highCutHz, sampleRate, filters);

        int count = Math.Min(freqHz.Length, responseDb.Length);
        for (int i = 0; i < count; i++)
        {
            float db = preampDb;
            for (int f = 0; f < FilterCount; f++) db += filters[f].MagnitudeDb(freqHz[i], sampleRate);
            responseDb[i] = db;
        }
    }
}