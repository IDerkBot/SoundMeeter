using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Компрессор входного стрипа (SM-B05): огибающая по пику со сглаживанием
/// attack/release, мягкое колено, makeup-gain.
///
/// Каналы связаны по детектору (берётся максимум L/R): иначе компрессор «гуляет»
/// на стерео и при появлении сигнала в одном канале второй остаётся несжатым —
/// на слух это binaural beating. Параметры читаются из модели на каждом пакете,
/// поэтому правка крутилки слышна сразу и без пересоздания аудиопотока. Выключение — не «обход», а плавный
/// сход рампы за <see cref="RampSamples"/> (5 мс, требование SM-B05 про
/// щелчковое переключение): скачок усиления на пакете слышен как щелчок.
/// </summary>
public sealed class CompressorDsp
{
    /// <summary>Длительность сглаживания включения/выключения, сэмплов (5 мс).</summary>
    private const int RampSamples = 240;

    /// <summary>Ширина мягкого колена, дБ. Фиксирована, чтобы не плодить крутилки.</summary>
    private const float KneeDb = 2f;

    /// <summary>Ниже этого уровня (дБ) сигнал считается тишиной: логарифм от нуля
    /// дал бы −∞, из которого нельзя посчитать ослабление.</summary>
    private const float FloorDb = -90f;

    /// <summary>Сглаживание makeup-gain при правке крутилки, мс.</summary>
    private const float MakeupSmoothMs = 15f;

    private readonly InputChannelModel _model;

    private float _envelope;
    private float _gain = 1f;
    private float _ramp;
    private bool _primed;

    public CompressorDsp(InputChannelModel model) => _model = model;

    /// <summary>Обрабатывает пакет на месте. Всегда ровно <paramref name="frames"/> кадров.</summary>
    public void Process(float[] stereo, int frames)
    {
        int samples = frames * 2;
        if (samples > stereo.Length) samples = stereo.Length & ~1;
        if (samples <= 0) return;

        float thresholdDb = Clamp(_model.CompressorThresholdDb, -60f, 0f);
        float ratio = Clamp(_model.CompressorRatio, 1f, 20f);
        float attack = 1f - OnePoleCoefficient(Clamp(_model.CompressorAttackMs, 0.1f, 100f));
        float release = 1f - OnePoleCoefficient(Clamp(_model.CompressorReleaseMs, 10f, 1000f));
        float makeupCoef = 1f - OnePoleCoefficient(MakeupSmoothMs);
        float makeup = DbToLinear(Clamp(_model.CompressorMakeupDb, -12f, 24f));

        // Рампа включения/выключения: линейный сход к цели за 5 мс.
        float rampStep = 1f / RampSamples;
        float rampTarget = _model.CompressorEnabled ? 1f : 0f;
        if (!_primed)
        {
            _ramp = rampTarget;
            _primed = true;
        }

        for (int i = 0; i < samples; i += 2)
        {
            _ramp = MoveTowards(_ramp, rampTarget, rampStep);

            float left = stereo[i];
            float right = stereo[i + 1];
            float detect = MathF.Max(MathF.Abs(left), MathF.Abs(right));

            // Огибающая: быстрый подъём на пиках, медленный спад.
            _envelope += (detect - _envelope) * (detect > _envelope ? attack : release);

            float overDb = ToDb(_envelope) - thresholdDb;
            float reductionDb = 0f;
            if (overDb > -KneeDb * 0.5f)
            {
                float slope = 1f - 1f / ratio;
                if (2f * overDb > KneeDb)
                {
                    reductionDb = -overDb * slope;
                }
                else
                {
                    // Мягкое колено: вход в сжатие и выход из него без излома.
                    float knee = overDb + KneeDb * 0.5f;
                    reductionDb = -(knee * knee) / (2f * KneeDb) * slope;
                }
            }

            float target = DbToLinear(reductionDb) * makeup;
            _gain += (target - _gain) * makeupCoef;

            // Кросфейд сухого и сжатого: gain ≤ 1 при сжатии, поэтому формула
            // 1 + ramp·(gain − 1) даёт ровно линейную интерполяцию, а сухой
            // сигнал хранить не нужно.
            float g = 1f + _ramp * (_gain - 1f);
            stereo[i] = left * g;
            stereo[i + 1] = right * g;
        }
    }

    /// <summary>Сбрасывает состояние (новый источник — с нуля, без щелчка).</summary>
    public void Reset()
    {
        _envelope = 0f;
        _gain = 1f;
        _ramp = 0f;
        _primed = false;
    }

    private static float OnePoleCoefficient(float ms) =>
        ms <= 0f ? 1f : 1f - MathF.Exp(-1000f / (ms * InputSource.SampleRate));

    private static float MoveTowards(float value, float target, float step) =>
        value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);

    private static float ToDb(float linear) =>
        linear <= 0f ? FloorDb : 20f * MathF.Log10(linear);

    internal static float Clamp(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    internal static float DbToLinear(float db) =>
        db <= -60f ? 0f : MathF.Pow(10f, db / 20f);
}
