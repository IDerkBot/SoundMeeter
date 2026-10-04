using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Денойзер входного канала на движке RNNoise (CPU).
///
/// Конвейер на каждый кадр = 480 сэмплов (10 мс @ 48 кГц):
///   1. RNNoise      — подавление шума;
///   2. Noise Remover — кросфейд «сухой»/«денойзерный» (0..100 %);
///   3. Formant EQ   — три пика (500 Гц / 1.5 кГц / 3.5 кГц) + общий makeup-gain;
///   4. Dry / Wet    — финальный кросфейд исходного и обработанного сигнала.
///
/// Ключевые инварианты — каждый из них по отдельности ломает шумоподавление:
///
/// * <b>Выравнивание по времени.</b> У RNNoise есть собственная групповая задержка
///   (около половины кадра STFT, и она зависит от частоты). «Сухой» сигнал для
///   кросфейдов берётся как вход, задержанный на ту же величину; значение
///   уточняется на лету по корреляции (см. <c>TrackDryDelay</c>). Если отступить
///   на целый кадр (как было раньше), «сухой» и «денойзерный» сигналы разойдутся
///   по времени, и кросфейд сложит их в противофазе: вместо плавного перехода
///   Noise Remover — немонотонная «громкость» с провалами до −25 дБ в середине
///   диапазона, и подавление шума работает наоборот.
///
/// * <b>Постоянная задержка.</b> Активный путь задерживает сигнал на величину
///   <c>_dryDelay</c> (~5 мс) независимо от размера входного буфера. Поток в
///   кольцевой буфер идёт с постоянной скоростью: нет накопления дрейфа и нет
///   «залипаний» на коротких батчах.
///
/// * <b>Выход никогда не длиннее входа.</b> <see cref="Process"/> всегда
///   возвращает ровно <c>frames</c>: нехватку в очереди выхода закрывает тишина,
///   излишек отбрасывается. В прошлой версии выход мог оказаться длиннее входа,
///   писал за границу буфера вызывающего кода, а исключение глушил catch в
///   InputSource — молча терялся целый батч аудио.
///
/// Выключенный денойзер — сигнал без задержки и без потерь; при переключении
/// состояние конвейера сбрасывается, а стык сглаживается коротким кросфейдом,
/// чтобы не было щелчка от скачка по времени.
/// </summary>
public sealed class DenoiserDsp : IDisposable
{
    private const int FrameSize = RnNoiseInterop.FrameSize;

    /// <summary>Сэмплов в одном кадре с учётом интерливинга (L+R).</summary>
    private const int FrameFloats = FrameSize * 2;

    /// <summary>Половина кадра. Групповая задержка RNNoise (окно STFT 480 с
    /// перекрытием 50 %) — примерно 240 сэмплов; это стартовое значение задержки
    /// «сухого» сигнала, далее она уточняется по корреляции.</summary>
    private const int HalfFrame = FrameSize / 2;

    /// <summary>Границы подстройки задержки «сухого» сигнала.</summary>
    private const float MinDryDelay = HalfFrame - 40f;
    private const float MaxDryDelay = HalfFrame + 40f;

    /// <summary>Глубина очередей в кадрах. 4 кадра = 40 мс — с большим запасом
    /// относительно максимального размера батча WASAPI в общем режиме.</summary>
    private const int QueueFrames = 4;
    private const int QueueCapacity = QueueFrames * FrameFloats;

    private const float Q = 1.0f;
    private const float LowFreq = 500f;
    private const float MidFreq = 1500f;
    private const float HighFreq = 3500f;

    /// <summary>Длительность сглаживания при включении/выключении, сэмплов.</summary>
    private const int BlendSamples = FrameSize * 3;

    /// <summary>Ниже этого порога считаем субнормалью и гасим (denormal-ловушка CPU).</summary>
    private const float DenormalFloor = 1e-20f;

    private readonly InputChannelModel _model;

    private IntPtr _rnState;

    /// <summary>Задержка «сухого» сигнала внутри кадра, сэмплов. Уточняется
    /// в <c>TrackDryDelay</c> под конкретную сборку RNNoise.</summary>
    private float _dryDelay = HalfFrame;

    // Кольцевые очереди на кадры: вход ещё не обработан / выход готов к выдаче.
    private readonly float[] _inQueue = new float[QueueCapacity];
    private int _inCount;
    private int _inHead;

    private readonly float[] _outQueue = new float[QueueCapacity];
    private int _outCount;
    private int _outHead;

    // Кадр, подаваемый в RNNoise на этом вызове.
    private readonly float[] _dryL = new float[FrameSize];
    private readonly float[] _dryR = new float[FrameSize];

    // Предыдущий кадр: нужен, чтобы «сухой» сигнал можно было прочитать с
    // задержкой, большей, чем ноль, не выходя за границы текущего кадра.
    // Обновляется ПОСЛЕ смешивания.
    private readonly float[] _prevL = new float[FrameSize];
    private readonly float[] _prevR = new float[FrameSize];

    // Результат RNNoise (выровнен с _prevL/_prevR) и финальный обработанный кадр.
    private readonly float[] _rnL = new float[FrameSize];
    private readonly float[] _rnR = new float[FrameSize];
    private readonly float[] _resL = new float[FrameSize];
    private readonly float[] _resR = new float[FrameSize];

    // Кадр в масштабе RNNoise (±32768): модель ждёт именно такого входа.
    private readonly float[] _scL = new float[FrameSize];
    private readonly float[] _scR = new float[FrameSize];

    private BiquadFilter _eqLowL, _eqMidL, _eqHighL;
    private BiquadFilter _eqLowR, _eqMidR, _eqHighR;
    private float _eqLow, _eqMid, _eqHigh, _groupDb;
    private float _makeup = 1f;
    private bool _eqPrimed;

    private float _speechProb;
    private bool _wasActive;

    // Сглаживание стыка при переключении: держим последний выданный сэмпл
    // и «доезжаем» от него к текущему входу.
    private float _lastL, _lastR;
    private int _blendLeft;

    /// <summary>Вероятность речи от RNNoise (0 = шум, 1 = речь), сглаженная.</summary>
    public float SpeechProbability => _speechProb;

    public DenoiserDsp(InputChannelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (InputSource.SampleRate != RnNoiseInterop.SampleRate)
            throw new NotSupportedException(
                $"RNNoise requires {RnNoiseInterop.SampleRate} Hz, capture runs at {InputSource.SampleRate} Hz.");

        _model = model;

        try
        {
            _rnState = RnNoiseInterop.Create();
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "rnnoise.dll not found. Check that the YellowDogMan.RRNoise.NET runtime asset is deployed.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException("rnnoise.dll does not export the expected RNNoise entry points.", ex);
        }

        if (_rnState == IntPtr.Zero)
            throw new InvalidOperationException("rnnoise_create() failed.");
    }

    /// <summary>
    /// Обрабатывает стерео-буфер 48 кГц на месте. Всегда возвращает
    /// <paramref name="frames"/>: сигнал не растягивается и не теряется.
    /// </summary>
    public int Process(float[] stereo, int frames)
    {
        if (frames <= 0) return 0;

        float amount = Math.Clamp(_model.DenoiserNoiseRemover, 0f, 100f) / 100f;
        float dryWet = Math.Clamp(_model.DenoiserDryWet, 0f, 100f) / 100f;
        bool active = _model.DenoiserEnabled && dryWet > 0f;

        if (!active)
        {
            if (_wasActive)
            {
                // Возврат с задержки (~5 мс) на нулевую — это скачок по времени.
                // Гасим его коротким кросфейдом от последнего выданного сэмпла.
                _blendLeft = BlendSamples;
                ResetPipeline();
                _wasActive = false;
            }
            return Bypass(stereo, frames);
        }

        _wasActive = true;
        _blendLeft = 0;

        UpdateEq(
            Math.Clamp(_model.DenoiserFormantLowDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantMidDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantHighDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantGroupDb, -12f, 12f));

        // Первые ~240 сэмплов после включения опираются на «мокрый» сигнал,
        // соответствующий началу потока, где его ещё нет, поэтому они приглушены.
        // Это нормальное поведение любого денойзера с задержкой в кадр; отдельный
        // кадр «заливки» не нужен — он лишь добавил бы ещё 10 мс задержки.
        for (int i = 0; i < frames; i++)
        {
            if (!EnqueueSample(_inQueue, ref _inCount, ref _inHead, stereo[i * 2], stereo[i * 2 + 1]))
                break; // переполнение: вход пришёл быстрее выхода, лишнее отбрасываем
        }

        while (_inCount >= FrameFloats && _outCount + FrameFloats <= QueueCapacity)
        {
            DequeueFrame(_inQueue, ref _inCount, ref _inHead, _dryL, _dryR);
            ProcessFrame(amount, dryWet);
            EnqueueFrame(_outQueue, ref _outCount, ref _outHead, _resL, _resR);
        }

        // Забираем ровно frames сэмплов: нехватку закрываем тишиной,
        // излишек отбрасываем, чтобы задержка не «плыла».
        for (int i = 0; i < frames; i++)
        {
            float l, r;
            if (_outCount > 0)
                DequeueSample(_outQueue, ref _outCount, ref _outHead, out l, out r);
            else
                l = r = 0f;

            _lastL = l;
            _lastR = r;
            stereo[i * 2] = l;
            stereo[i * 2 + 1] = r;
        }

        return frames;
    }

    private int Bypass(float[] stereo, int frames)
    {
        if (_blendLeft <= 0) return frames;

        for (int i = 0; i < frames; i++)
        {
            if (_blendLeft <= 0) break;
            float t = 1f - _blendLeft / (float)BlendSamples;
            _blendLeft--;
            stereo[i * 2] = _lastL + (stereo[i * 2] - _lastL) * t;
            stereo[i * 2 + 1] = _lastR + (stereo[i * 2 + 1] - _lastR) * t;
            _lastL = stereo[i * 2];
            _lastR = stereo[i * 2 + 1];
        }
        return frames;
    }

    /// <summary>
    /// Обрабатывает текущий кадр (<c>_dryL/_dryR</c>) и кладёт результат
    /// в <c>_resL/_resR</c>. Вызывается только при заполненной входной очереди.
    /// </summary>
    private void ProcessFrame(float amount, float dryWet)
    {
        // ВАЖНО 1: RNNoise ожидает сигнал в масштабе ±32768. Без домножения
        // модель видит «тишину» и просто пропускает вход — подавления нет.
        for (int i = 0; i < FrameSize; i++)
        {
            _scL[i] = _dryL[i] * RnNoiseInterop.SignalScale;
            _scR[i] = _dryR[i] * RnNoiseInterop.SignalScale;
        }

        // ВАЖНО 2 (выравнивание по времени). Результат RNNoise сдвинут внутри
        // кадра на свою групповую задержку — примерно половину окна STFT. Если
        // «сухой» и «денойзерный» сигналы не совпадают по времени, кросфейд
        // складывает их в противофазе: немонотонная «громкость», провалы
        // уровня в середине диапазона Noise Remover, и подавление шума
        // работает наоборот. Задержка не берётся «на глаз» — она уточняется по
        // корреляции сухого и денойзерного сигналов (TrackDryDelay), потому
        // что зависит от частоты и от сборки RNNoise.
        float prob = RnNoiseInterop.ProcessFrame(_rnState, _scL, _rnL);
        RnNoiseInterop.ProcessFrame(_rnState, _scR, _rnR);
        _speechProb += (Math.Clamp(prob, 0f, 1f) - _speechProb) * 0.2f;
        TrackDryDelay();

        const float inv = RnNoiseInterop.SignalScaleInv;
        for (int i = 0; i < FrameSize; i++)
        {
            float dL = Delayed(_prevL, _dryL, i, _dryDelay);
            float dR = Delayed(_prevR, _dryR, i, _dryDelay);
            // Обратный масштаб сложен в кросфейд — лишнего прохода нет.
            float wL = _rnL[i] * inv;
            float wR = _rnR[i] * inv;

            // 1) Noise Remover. Линейный кросфейд: «сухой» и «денойзерный»
            //    сигналы коррелированы, поэтому equal-power дал бы подъём
            //    уровня в середине диапазона.
            float mL = dL + (wL - dL) * amount;
            float mR = dR + (wR - dR) * amount;

            // 2) Formant EQ применяется к результату, а не только к «мокрому»:
            //    иначе при Noise Remover = 0 эквалайзер пропадал бы совсем.
            mL = _eqLowL.Process(_eqMidL.Process(_eqHighL.Process(mL))) * _makeup;
            mR = _eqLowR.Process(_eqMidR.Process(_eqHighR.Process(mR))) * _makeup;

            // 3) Dry / Wet.
            _resL[i] = Sanitize(dL + (mL - dL) * dryWet);
            _resR[i] = Sanitize(dR + (mR - dR) * dryWet);
        }

        // Сдвиг конвейера строго ПОСЛЕ смешивания, иначе «сухой» сигнал
        // перестал бы соответствовать результату RNNoise.
        Array.Copy(_dryL, _prevL, FrameSize);
        Array.Copy(_dryR, _prevR, FrameSize);
    }

    /// <summary>
    /// Читает «сухой» сигнал с задержкой <paramref name="delay"/> сэмплов внутри
    /// кадра. Таймлайн кадра: <c>_prev*</c> — предыдущий кадр (позиции
    /// [-FrameSize, 0)), <c>_dry*</c> — текущий ([0, FrameSize)). Дробная
    /// задержка берётся кубической интерполяцией Лагранжа по 4 отсчётам.
    /// </summary>
    private static float Delayed(float[] prev, float[] cur, int i, float delay)
    {
        double pos = i - delay;
        int j = (int)Math.Floor(pos);
        double t = pos - j;

        float c0 = Sample(prev, cur, j - 1);
        float c1 = Sample(prev, cur, j);
        float c2 = Sample(prev, cur, j + 1);
        float c3 = Sample(prev, cur, j + 2);

        // коэффициенты Лагранжа для узлов -1, 0, 1, 2
        double a0 = -t * (t - 1) * (t - 2) / 6.0;
        double a1 = (t + 1) * (t - 1) * (t - 2) / 2.0;
        double a2 = -(t + 1) * t * (t - 2) / 2.0;
        double a3 = (t + 1) * t * (t - 1) / 6.0;

        return (float)(a0 * c0 + a1 * c1 + a2 * c2 + a3 * c3);
    }

    private static float Sample(float[] prev, float[] cur, int idx) =>
        idx < 0 ? prev[idx + FrameSize] : cur[idx];

    /// <summary>
    /// Уточняет задержку «сухого» сигнала по максимуму корреляции с выходом
    /// RNNoise: параболическая интерполяция по трём точкам (задержка ±1 сэмпл)
    /// даёт субсэмпловую поправку, она применяется с коэффициентом 0.25 —
    /// то есть подстройка плавная, за доли секунды. В тишине корреляция
    /// шумовая, поэтому при низком уровне задержка не трогается.
    /// </summary>
    private void TrackDryDelay()
    {
        float energy = 0f;
        for (int i = 0; i < FrameSize; i++) energy += _rnL[i] * _rnL[i];
        if (energy < FrameSize * 16f) return; // тишина — оценивать нечего

        float cm = Correlate(_dryDelay - 1f);
        float c0 = Correlate(_dryDelay);
        float cp = Correlate(_dryDelay + 1f);

        // Вершина параболы по трём точкам (x = -1, 0, +1).
        float denom = cm - 2f * c0 + cp;
        if (MathF.Abs(denom) < 1e-6f) return;

        float offset = 0.5f * (cm - cp) / denom;
        if (!float.IsFinite(offset)) return;

        _dryDelay = Math.Clamp(_dryDelay + Math.Clamp(offset, -1f, 1f) * 0.25f, MinDryDelay, MaxDryDelay);
    }

    private float Correlate(float delay)
    {
        float sum = 0f;
        for (int i = 0; i < FrameSize; i++)
            sum += Delayed(_prevL, _dryL, i, delay) * _rnL[i];
        return sum;
    }

    private void UpdateEq(float lowDb, float midDb, float highDb, float groupDb)
    {
        if (_eqPrimed && _eqLow == lowDb && _eqMid == midDb && _eqHigh == highDb && _groupDb == groupDb)
            return;

        SetPeak(ref _eqLowL, LowFreq, lowDb);
        SetPeak(ref _eqMidL, MidFreq, midDb);
        SetPeak(ref _eqHighL, HighFreq, highDb);
        SetPeak(ref _eqLowR, LowFreq, lowDb);
        SetPeak(ref _eqMidR, MidFreq, midDb);
        SetPeak(ref _eqHighR, HighFreq, highDb);

        _eqLow = lowDb;
        _eqMid = midDb;
        _eqHigh = highDb;
        _groupDb = groupDb;
        _makeup = MathF.Pow(10f, groupDb / 20f);
        _eqPrimed = true;
    }

    /// <summary>
    /// Пик формантного EQ со сбросом состояния. Именно со сбросом: полосы
    /// пересчитываются на ходу, у денойзера за ними нечего тянуть, а оборванная
    /// нить фильтра слышна как щелчок. Эквалайзер стрипа так не делает — там
    /// сброса быть не должно (см. <see cref="BiquadFilter"/>).
    /// </summary>
    private void SetPeak(ref BiquadFilter filter, float freq, float gainDb)
    {
        filter.SetPeaking(freq, gainDb, Q, InputSource.SampleRate);
        filter.ResetState();
    }

    private static float Sanitize(float x) => float.IsFinite(x) && MathF.Abs(x) >= DenormalFloor ? x : 0f;

    private void ResetQueues()
    {
        _inCount = 0;
        _inHead = 0;
        _outCount = 0;
        _outHead = 0;
        Array.Clear(_prevL);
        Array.Clear(_prevR);
    }

    private void ResetPipeline()
    {
        ResetQueues();
        Array.Clear(_dryL);
        Array.Clear(_dryR);
        _eqLowL = _eqMidL = _eqHighL = default;
        _eqLowR = _eqMidR = _eqHighR = default;
        _eqPrimed = false;
        _dryDelay = HalfFrame;
    }

    // Очереди хранят сэмплы ИНТЕРЛИВИНГОМ (L0,R0,L1,R1,...) — в том же виде,
    // в каком приходит и уходит аудио. Поэтому кадр (480 сэмплов L и 480 R)
    // занимает FrameFloats ячеек, а чтение и запись кадра обязаны
    // де-/реинтерливить. Если читать кадр как сплошной блок, в денойзер
    // попадёт «L0,R0,L1,R1,...» — сигнал на удвоенной частоте: RNNoise
    // получит мусор, и подавление шума не заработает.
    private static void EnqueueFrame(float[] queue, ref int count, ref int head, float[] l, float[] r)
    {
        if (count + FrameFloats > queue.Length) return;
        int tail = (head + count) % queue.Length;
        for (int i = 0; i < FrameSize; i++)
        {
            queue[tail + i * 2] = l[i];
            queue[tail + i * 2 + 1] = r[i];
        }
        count += FrameFloats;
    }

    private static void DequeueFrame(float[] queue, ref int count, ref int head, float[] l, float[] r)
    {
        for (int i = 0; i < FrameSize; i++)
        {
            l[i] = queue[head + i * 2];
            r[i] = queue[head + i * 2 + 1];
        }
        head = head + FrameFloats >= queue.Length ? 0 : head + FrameFloats;
        count -= FrameFloats;
    }

    private static bool EnqueueSample(float[] queue, ref int count, ref int head, float l, float r)
    {
        if (count + 2 > queue.Length) return false;
        int tail = (head + count) % queue.Length;
        queue[tail] = l;
        queue[tail + 1] = r;
        count += 2;
        return true;
    }

    private static void DequeueSample(float[] queue, ref int count, ref int head, out float l, out float r)
    {
        l = queue[head];
        r = queue[head + 1];
        head = head + 2 >= queue.Length ? 0 : head + 2;
        count -= 2;
    }

    public void Dispose()
    {
        RnNoiseInterop.Destroy(_rnState);
        _rnState = IntPtr.Zero;
    }
}
