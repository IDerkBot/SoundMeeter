using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Графический эквалайзер входного стрипа: десять пиков с шагом примерно в
/// октаву (31 Гц…16 кГц), срез снизу, срез сверху и общий makeup-gain.
///
/// Место в цепочке — до компрессора: срезы и пики правят форму сигнала, а
/// компрессор должен видеть уже выправленный уровень. Иначе он сжимал бы в том
/// числе то, что эквалайзер приподнял, и разборчивый голос после подъёма
/// середины тише бы «разжимался» обратно.
///
/// <para><b>Параметры читаются из модели на каждом пакете</b>, поэтому любая
/// крутилка или перетаскивание полосы слышны сразу, без пересоздания
/// аудиопотока.</para>
///
/// <para><b>Коэффициенты догоняют новую цель не сразу, а ступенчато.</b> Смена
/// коэффициентов биквада — это возмущение фильтра, и на резком скачке оно слышно
/// как щелчок. Поэтому цель достигается экспоненциально, шагами по
/// <see cref="UpdateInterval"/> сэмплов: за время сглаживания полоса доезжает от
/// прежнего значения к новому, и на слух это тот же плавный ход, что при движении
/// крутилки у любого прибора.</para>
///
/// <para><b>Полностью выключенный эквалайзер буфера не касается.</b> Не
/// «прогоняет сигнал через нулевые коэффициенты», а именно выходит: иначе стрип
/// без эквалайзера платил бы двадцать четыре биквада на каждый сэмпл и не был бы
/// побитово прозрачным (на этом стоит тест прозрачности выключенного эффекта).</para>
/// </summary>
public sealed class EqualizerDsp
{
    private const float SampleRate = InputSource.SampleRate;

    /// <summary>Длительность сглаживания включения/выключения, сэмплов (5 мс).</summary>
    private const int RampSamples = 240;

    /// <summary>
    /// Как часто коэффициенты догоняют цель, сэмплов. ≈1.3 мс при 48 кГц.
    ///
    /// Чаще — дорого (считать 12 биквадов на каждом шаге), реже (раз в пакет,
    /// 10 мс) — слышно ступенями: соседние шаги отличаются на полдецибела, и на
    /// кривой с десятью пиками это шум.
    /// </summary>
    private const int UpdateInterval = 64;

    /// <summary>
    /// Постоянная времени сглаживания коэффициентов, мс: за это время цель
    /// достигается примерно на 63 %.
    /// </summary>
    private const float SmoothTauMs = 40f;

    /// <summary>
    /// Доля пути к цели за один шаг сглаживания. Считается один раз: при
    /// <see cref="UpdateInterval"/> = 64 и tau = 40 мс это ≈3.3 %, то есть шаг
    /// заметно мельче шага мыши при перетаскивании полосы.
    /// </summary>
    private static readonly float SmoothCoef =
        1f - MathF.Exp(-UpdateInterval / (SmoothTauMs * 0.001f * SampleRate));

    private readonly InputChannelModel _model;

    // По одному набору фильтров на канал: коэффициенты одинаковые, состояние
    // разное, поэтому общее состояние на стерео не годится.
    private readonly BiquadFilter[] _left = new BiquadFilter[EqualizerResponse.FilterCount];
    private readonly BiquadFilter[] _right = new BiquadFilter[EqualizerResponse.FilterCount];

    // Текущие (сглаженные) значения параметров и общий makeup-gain.
    private readonly float[] _bandDb = new float[InputChannelModel.EqBandCount];
    private float _preampDb;
    private float _lowCutHz = InputChannelModel.EffectDefaults.EqLowCutHz;
    private float _highCutHz = InputChannelModel.EffectDefaults.EqHighCutHz;
    private float _preampLinear = 1f;

    /// <summary>Кросфейд включения/выключения, 0…1.</summary>
    private float _wet;

    private int _untilUpdate;
    private bool _primed;

    /// <summary>
    /// Следующий шаг коэффициентов берёт цель сразу, без сглаживания. Ставится
    /// перед первым пакетом и уходом в обход: на старте источника доезжать
    /// полсекунды незачем, а при включении стык и так закрывает кросфейд — иначе
    /// полосы, правленные при выключенном EQ, были бы слышны не с той кривой.
    /// </summary>
    private bool _snapTargets = true;

    public EqualizerDsp(InputChannelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    /// <summary>Обрабатывает пакет на месте. Длина и число кадров не меняются.</summary>
    public void Process(float[] stereo, int frames)
    {
        int samples = frames * 2;
        if (samples > stereo.Length) samples = stereo.Length & ~1;
        if (samples <= 0) return;

        float rampTarget = _model.EqEnabled ? 1f : 0f;
        if (!_primed)
        {
            // Первый пакет после старта источника: включаться «с нуля» не из чего,
            // поэтому кросфейд сразу на целевое значение, а не с тишины.
            _wet = rampTarget;
            _primed = true;
        }

        if (_wet <= 0f && rampTarget <= 0f)
        {
            _snapTargets = true;
            return;
        }

        float rampStep = 1f / RampSamples;
        for (int i = 0; i < samples; i += 2)
        {
            _wet = MoveTowards(_wet, rampTarget, rampStep);

            if (--_untilUpdate <= 0)
            {
                _untilUpdate = UpdateInterval;
                UpdateFilters();
            }

            float dryL = stereo[i];
            float dryR = stereo[i + 1];
            float wetL = ProcessChain(_left, dryL);
            float wetR = ProcessChain(_right, dryR);

            if (_wet >= 1f)
            {
                stereo[i] = wetL;
                stereo[i + 1] = wetR;
            }
            else
            {
                stereo[i] = dryL + (wetL - dryL) * _wet;
                stereo[i + 1] = dryR + (wetR - dryR) * _wet;
            }
        }
    }

    /// <summary>
    /// Сброс состояния: новый запуск источника не должен продолжать старый хвост
    /// фильтров. Параметры из модели на следующем же пакете пересчитаются сами.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _left.Length; i++)
        {
            _left[i].ResetState();
            _right[i].ResetState();
        }

        _preampLinear = 1f;
        _wet = 0f;
        _primed = false;
        _snapTargets = true;
    }

    /// <summary>
    /// Шаг сглаживания: подтянуть текущие значения к целевым и пересчитать
    /// коэффициенты. Состояние фильтров при этом НЕ обнуляется — фильтр звучит
    /// дальше по той же нити, иначе смена коэффициентов сама стала бы щелчком.
    /// </summary>
    private void UpdateFilters()
    {
        float coef = _snapTargets ? 1f : SmoothCoef;
        _snapTargets = false;

        float limit = InputChannelModel.EqBandGainLimitDb;
        for (int i = 0; i < _bandDb.Length; i++)
        {
            float target = Clamp(_model.GetEqBand(i), 0f, -limit, limit);
            _bandDb[i] += (target - _bandDb[i]) * coef;
        }

        float preampLimit = InputChannelModel.EqPreampLimitDb;
        float preampTarget = Clamp(_model.EqPreampDb, 0f, -preampLimit, preampLimit);
        _preampDb += (preampTarget - _preampDb) * coef;
        _preampLinear = CompressorDsp.DbToLinear(_preampDb);

        // Срезы гладятся в частоте, а не в логарифме: их диапазон узкий
        // (20…300 Гц и 3…20 кГц), разница в третьем порядке на слух не слышна,
        // зато сглаживание остаётся тем же кодом, что и у полос.
        float lowTarget = Clamp(_model.EqLowCutHz, InputChannelModel.EqLowCutMinHz,
            InputChannelModel.EqLowCutMinHz, InputChannelModel.EqLowCutMaxHz);
        float highTarget = Clamp(_model.EqHighCutHz, InputChannelModel.EqHighCutMaxHz,
            InputChannelModel.EqHighCutMinHz, InputChannelModel.EqHighCutMaxHz);
        _lowCutHz += (lowTarget - _lowCutHz) * coef;
        _highCutHz += (highTarget - _highCutHz) * coef;

        EqualizerResponse.Compute(_bandDb, _lowCutHz, _highCutHz, SampleRate, _left);
        EqualizerResponse.Compute(_bandDb, _lowCutHz, _highCutHz, SampleRate, _right);
    }

    private float ProcessChain(BiquadFilter[] filters, float x)
    {
        for (int i = 0; i < filters.Length; i++) x = filters[i].Process(x);
        return x * _preampLinear;
    }

    /// <summary>
    /// Значение из пресета в рабочем диапазоне. Мусор в файле настроек (правлен
    /// руками) заменяется <paramref name="fallback"/> — для частот среза это
    /// «выключено», а не 0 Гц.
    /// </summary>
    private static float Clamp(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static float MoveTowards(float value, float target, float step) =>
        MathF.Abs(target - value) <= step ? target : value + MathF.Sign(target - value) * step;
}