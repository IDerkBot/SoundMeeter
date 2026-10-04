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
/// <para><b>Выравнивание по времени.</b> У RNNoise есть собственная групповая
/// задержка, и она НЕ равна половине кадра: для <c>rnnoise.dll</c> из
/// YellowDogMan.RRNoise.NET измерено <see cref="RnNoiseDelay"/> = 960 сэмплов
/// (20 мс, два кадра STFT). «Сухой» сигнал для кросфейдов берётся как вход,
/// задержанный на ту же величину, и она уточняется на лету по корреляции
/// (<see cref="TrackDryDelay"/>), но начальное значение и границы поиска обязаны
/// быть около неё: <i>«на глаз»</i> здесь не работает. Расстройка в 720 сэмплов
/// (как было) — это ровно половина периода 300 Гц, и кросфейд складывает
/// «сухой» и «денойзерный» сигналы <b>в противофазе</b>: на 50 % Noise Remover
/// ровнотон 500 Гц гаснет на −64 дБ, а на 40 % и 60 % проваливается до −27 дБ.
/// Звучит это как треск, тем громче, чем дальше сдвинут крутилка.</para>
///
/// <para><b>Очередь больше пакета.</b> WASAPI в общем режиме отдаёт захват
/// пакетами по <see cref="MaxPacketFrames"/> — до 100 мс, потому что
/// <c>WasapiCapture</c> создаётся без параметра латентности и NAudio берёт
/// дефолт <c>audioBufferMillisecondsLength: 100</c>. Входная очередь держит
/// <i>весь</i> пакет: на каждый вызов сначала забирается вход, потом отдаётся
/// ровно <c>frames</c>. Промежуточный вариант («очередь на 4 кадра, хвост
/// пакета отбросить, нехватку выхода закрыть нулями») вставлял до 60 % каждого
/// пакета цифровой тишины — то есть треск был ещё и при любых настройках
/// кросфейда, а не только на его середине.</para>
///
/// <para><b>Постоянная задержка.</b> Активный путь задерживает сигнал на
/// величину <c>_dryDelay</c> (~20 мс) независимо от размера входного буфера.
/// Поток в кольцевой буфер идёт с постоянной скоростью: нет накопления дрейфа и
/// нет «залипаний» на коротких батчах.</para>
///
/// <para><b>Выход никогда не длиннее входа.</b> <see cref="Process"/> всегда
/// возвращает ровно <c>frames</c>: нехватку в очереди выхода закрывает тишина
/// (и считает её в <see cref="UnderrunFrames"/> — на этот счёт есть тест),
/// излишек отбрасывается.</para>
///
/// Выключенный денойзер — сигнал без задержки и без потерь; при переключении
/// состояние конвейера сбрасывается, а стык сглаживается коротким кросфейдом
/// (в обе стороны), чтобы не было щелчка от скачка по времени.
/// </summary>
public sealed class DenoiserDsp : IDisposable
{
    private const int FrameSize = RnNoiseInterop.FrameSize;

    /// <summary>Сэмплов в одном кадре с учётом интерливинга (L+R).</summary>
    private const int FrameFloats = FrameSize * 2;

    /// <summary>
    /// Групповая задержка <c>rnnoise_process_frame</c>, сэмплов. Измерена для
    /// <c>rnnoise.dll</c> из YellowDogMan.RRNoise.NET 0.1.9: два кадра STFT по
    /// 10 мс, итого 20 мс. Значение нужно не «примерно», а точно: «сухой» и
    /// «денойзерный» сигналы складываются кросфейдом, и расстройка в половину
    /// периода низкочастотной составляющей даёт не сумму, а вычитание.
    /// </summary>
    private const float RnNoiseDelay = FrameSize * 2;

    /// <summary>Насколько подстройка задержки может уйти от номинала, сэмплов (±2 мс).</summary>
    private const float DryDelayTolerance = 96f;

    /// <summary>Границы подстройки задержки «сухого» сигнала.</summary>
    private const float MinDryDelay = RnNoiseDelay - DryDelayTolerance;
    private const float MaxDryDelay = RnNoiseDelay + DryDelayTolerance;

    /// <summary>
    /// Кадров «сухого» сигнала в кольце истории. Читать приходится с задержкой
    /// до <see cref="MaxDryDelay"/> (1056 сэмплов ≈ 2.2 кадра) плюс три отсчёта
    /// интерполяции; четыре кадра дают на это запас. Меньше трёх нельзя: задержка
    /// в 2+ кадра физически не помещается в пару «предыдущий + текущий».
    /// </summary>
    private const int HistoryFrames = 4;
    private const int HistorySamples = HistoryFrames * FrameSize;

    /// <summary>
    /// Максимальный пакет, который держит входная очередь, в кадрах: 250 мс.
    /// NAudio без явного параметра латентности отдаёт по 100 мс, но размер
    /// батча растёт и сам по себе — когда захватывающий поток хоть раз отстал
    /// (см. <c>InputSource.TrackPacketSize</c>). Очередь должна переваривать
    /// батч целиком, поэтому берётся с запасом; в установившемся режиме она
    /// пуста и на задержку не влияет.
    ///
    /// ВНИМАНИЕ: значение обязано делиться на <see cref="FrameSize"/> без
    /// остатка. Тогда <c>head</c> в очереди, где сэмплы ходят по два, всегда
    /// остаётся на границе кадра, и кадр физически не может лечь поперёк конца
    /// кольца. <see cref="DequeueFrame"/> на это не рассчитывает.
    /// </summary>
    public const int MaxPacketFrames = 12000;

    private const int QueueFloats = MaxPacketFrames * 2;

    private const float Q = 1.0f;
    private const float LowFreq = 500f;
    private const float MidFreq = 1500f;
    private const float HighFreq = 3500f;

    /// <summary>Постоянная времени сглаживания формантного EQ, мс. Шаг EQ делается
    /// один раз на кадр (10 мс), поэтому 60 мс — это шесть шагов до цели.</summary>
    private const float EqSmoothMs = 60f;

    /// <summary>Доля пути к цели EQ за один кадр. Считается один раз.</summary>
    private static readonly float EqSmoothCoef =
        1f - MathF.Exp(-FrameSize / (EqSmoothMs * 0.001f * InputSource.SampleRate));

    /// <summary>Длительность сглаживания стыка при включении/выключении, сэмплов.</summary>
    private const int BlendSamples = FrameSize * 3;

    /// <summary>
    /// Нарастание сигнала после включения, сэмплов. Кольцо истории только что
    /// обнулено, первые кадры «сухого» сигнала — тишина; без нарастания
    /// сигнал появился бы на них скачком от нуля.
    /// </summary>
    private const int FadeInSamples = FrameSize;

    /// <summary>Ниже этого порога считаем субнормалью и гасим (denormal-ловушка CPU).</summary>
    private const float DenormalFloor = 1e-20f;

    private readonly InputChannelModel _model;

    private IntPtr _rnStateL;
    private IntPtr _rnStateR;

    /// <summary>Задержка «сухого» сигнала внутри кадра, сэмплов. Уточняется
    /// в <c>TrackDryDelay</c> под конкретную сборку RNNoise.</summary>
    private float _dryDelay = RnNoiseDelay;

    // Кольцевые очереди на кадры: вход ещё не обработан / выход готов к выдаче.
    private readonly float[] _inQueue = new float[QueueFloats];
    private int _inCount;
    private int _inHead;

    private readonly float[] _outQueue = new float[QueueFloats];
    private int _outCount;
    private int _outHead;

    // Кадр, подаваемый в RNNoise на этом вызове.
    private readonly float[] _dryL = new float[FrameSize];
    private readonly float[] _dryR = new float[FrameSize];

    // Кольцо истории «сухого» сигнала: нужны несколько кадров назад, потому что
    // задержка RNNoise больше одного кадра. _histHead указывает на текущий кадр;
    // кадр, лежащий на k кадров раньше, — на _histHead - k*FrameSize по кольцу.
    private readonly float[] _histL = new float[HistorySamples];
    private readonly float[] _histR = new float[HistorySamples];
    private int _histHead;

    // Результат RNNoise (выровнен с историей) и финальный обработанный кадр.
    private readonly float[] _rnL = new float[FrameSize];
    private readonly float[] _rnR = new float[FrameSize];
    private readonly float[] _resL = new float[FrameSize];
    private readonly float[] _resR = new float[FrameSize];

    // Кадр в масштабе RNNoise (±32768): модель ждёт именно такого входа.
    private readonly float[] _scL = new float[FrameSize];
    private readonly float[] _scR = new float[FrameSize];

    private BiquadFilter _eqLowL, _eqMidL, _eqHighL;
    private BiquadFilter _eqLowR, _eqMidR, _eqHighR;

    // Текущее (сглаженное) и целевое значения EQ. Разделены, потому что
    // UpdateEq приходит на каждый пакет, а фильтр пересчитывается на каждый
    // кадр и не мгновенно.
    private float _eqLow, _eqMid, _eqHigh, _groupDb;
    private float _targetLowDb, _targetMidDb, _targetHighDb, _targetGroupDb;
    private float _makeup = 1f;
    private bool _eqPrimed;

    private float _speechProb;
    private bool _wasActive;

    // Сглаживание стыка при выключении: держим последний выданный сэмпл
    // и «доезжаем» от него к текущему входу.
    private float _lastL, _lastR;
    private int _blendLeft;
    private int _fadeIn;

    /// <summary>Вероятность речи от RNNoise (0 = шум, 1 = речь), сглаженная.</summary>
    public float SpeechProbability => _speechProb;

    /// <summary>
    /// Кадров, выданных тишиной из-за нехватки в очереди вывода. В установившемся
    /// режиме ноль: очередь входа переваривает пакет целиком. Неноль — значит
    /// пакет длиннее <see cref="MaxPacketFrames"/> либо поток захвата отстал.
    /// </summary>
    public long UnderrunFrames { get; private set; }

    /// <summary>Кадров, сброшенных на входе из-за переполнения очереди.</summary>
    public long OverrunFrames { get; private set; }

    public DenoiserDsp(InputChannelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (InputSource.SampleRate != RnNoiseInterop.SampleRate)
            throw new NotSupportedException(
                $"RNNoise requires {RnNoiseInterop.SampleRate} Hz, capture runs at {InputSource.SampleRate} Hz.");

        _model = model;

        try
        {
            // Состояния РОВНО ДВА, по одному на канал, и это не оптимизация:
            // rnnoise_state внутри хранит буферы перекрытия STFT и оценку шума.
            // Если прогнать оба канала через одно состояние, второй вызов
            // перезапишет буферы первого, и на выходе левый и правый каналы
            // окажутся разными сигналами: моно-микрофон (а он моно, и ещё есть
            // кнопка Mono) распадался на стерео из двух несовпадающих копий,
            // со сдвигом и разным подавлением. Слышно это как «робот» тем
            // сильнее, чем выше Noise Remover, потому что там выход целиком
            // берётся из этого испорченного состояния.
            _rnStateL = RnNoiseInterop.Create();
            if (_rnStateL == IntPtr.Zero)
                throw new InvalidOperationException("rnnoise_create() failed for the left channel.");
            _rnStateR = RnNoiseInterop.Create();
            if (_rnStateR == IntPtr.Zero)
                throw new InvalidOperationException("rnnoise_create() failed for the right channel.");
        }
        catch (DllNotFoundException ex)
        {
            ReleaseStates();
            throw new InvalidOperationException(
                "rnnoise.dll not found. Check that the YellowDogMan.RRNoise.NET runtime asset is deployed.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            ReleaseStates();
            throw new InvalidOperationException("rnnoise.dll does not export the expected RNNoise entry points.", ex);
        }
        catch
        {
            // Второе состояние не создалось — первое уже не вернуть в систему.
            ReleaseStates();
            throw;
        }
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
                // Возврат с задержки (~20 мс) на нулевую — это скачок по времени.
                // Гасим его коротким кросфейдом от последнего выданного сэмпла.
                _blendLeft = BlendSamples;
                ResetPipeline();
                _wasActive = false;
            }
            return Bypass(stereo, frames);
        }

        if (!_wasActive)
        {
            // Вход в активный путь: конвейер пуст, сигнал появится через задержку
            // RNNoise — нарастанием, иначе старт от нуля будет щелчком.
            _fadeIn = FadeInSamples;
            _wasActive = true;
        }
        _blendLeft = 0;

        UpdateEq(
            Math.Clamp(_model.DenoiserFormantLowDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantMidDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantHighDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantGroupDb, -12f, 12f));

        // Сначала весь вход, потом весь выход: так очередь входа работает
        // накопителем и пакет любой длины (в т.ч. 100 мс от WASAPI) выходит
        // без хвоста и без «дыр».
        for (int i = 0; i < frames; i++)
        {
            if (!EnqueueSample(_inQueue, ref _inCount, ref _inHead, stereo[i * 2], stereo[i * 2 + 1]))
            {
                OverrunFrames++;
                break; // переполнение: вход пришёл быстрее выхода, лишнее отбрасываем
            }
        }

        while (_inCount >= FrameFloats && _outCount + FrameFloats <= QueueFloats)
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
            {
                l = r = 0f;
                UnderrunFrames++;
            }

            if (_fadeIn > 0)
            {
                float fade = (FadeInSamples - _fadeIn) / (float)FadeInSamples;
                _fadeIn--;
                l *= fade;
                r *= fade;
            }

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
        AdvanceEq();

        // Текущий кадр — в кольцо истории, откуда «сухой» сигнал читается с
        // задержкой в несколько кадров (см. _dryDelay).
        Array.Copy(_dryL, 0, _histL, _histHead, FrameSize);
        Array.Copy(_dryR, 0, _histR, _histHead, FrameSize);

        // ВАЖНО 1: RNNoise ожидает сигнал в масштабе ±32768. Без домножения
        // модель видит «тишину» и просто пропускает вход — подавления нет.
        for (int i = 0; i < FrameSize; i++)
        {
            _scL[i] = _dryL[i] * RnNoiseInterop.SignalScale;
            _scR[i] = _dryR[i] * RnNoiseInterop.SignalScale;
        }

        // ВАЖНО 2 (выравнивание по времени). Результат RNNoise сдвинут внутри
        // кадра на свою групповую задержку — 20 мс для этой сборки, см.
        // RnNoiseDelay. Если «сухой» и «денойзерный» сигналы не совпадают по
        // времени, кросфейд складывает их в противофазе: немонотонная
        // «громкость», провалы уровня в середине диапазона Noise Remover, и
        // подавление шума работает наоборот. Задержка не берётся «на глаз» — она
        // уточняется по корреляции сухого и денойзерного сигналов
        // (TrackDryDelay), потому что зависит от сборки RNNoise.
        //
        // Каналы идут через РАЗНЫЕ состояния — см. комментарий в конструкторе.
        // Вероятность речи берётся по максимуму каналов: канал, где речь
        // есть, должен поднять индикацию, даже если во втором её не слышно.
        float probL = RnNoiseInterop.ProcessFrame(_rnStateL, _scL, _rnL);
        float probR = RnNoiseInterop.ProcessFrame(_rnStateR, _scR, _rnR);
        float prob = Math.Clamp(MathF.Max(probL, probR), 0f, 1f);
        _speechProb += (prob - _speechProb) * 0.2f;
        TrackDryDelay();

        const float inv = RnNoiseInterop.SignalScaleInv;
        for (int i = 0; i < FrameSize; i++)
        {
            float dL = DelayedL(i, _dryDelay);
            float dR = DelayedR(i, _dryDelay);
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

        // Кольцо истории сдвигается ПОСЛЕ смешивания, иначе «сухой» сигнал
        // перестал бы соответствовать результату RNNoise.
        _histHead += FrameSize;
        if (_histHead >= HistorySamples) _histHead = 0;
    }

    /// <summary>
    /// Читает «сухой» сигнал с задержкой <paramref name="delay"/> сэмплов внутри
    /// кадра. Таймлайн кадра: индекс 0 — текущий кадр (<c>_histHead</c> в кольце),
    /// отрицательные индексы — предыдущие кадры. Дробная задержка берётся
    /// кубической интерполяцией Лагранжа по 4 отсчётам.
    /// </summary>
    private float DelayedL(int i, float delay) => Delayed(_histL, i, delay);

    private float DelayedR(int i, float delay) => Delayed(_histR, i, delay);

    private float Delayed(float[] hist, int i, float delay)
    {
        double pos = i - delay;
        int j = (int)Math.Floor(pos);
        double t = pos - j;

        float c0 = Sample(hist, j - 1);
        float c1 = Sample(hist, j);
        float c2 = Sample(hist, j + 1);
        float c3 = Sample(hist, j + 2);

        // коэффициенты Лагранжа для узлов -1, 0, 1, 2
        double a0 = -t * (t - 1) * (t - 2) / 6.0;
        double a1 = (t + 1) * (t - 1) * (t - 2) / 2.0;
        double a2 = -(t + 1) * t * (t - 2) / 2.0;
        double a3 = (t + 1) * t * (t - 1) / 6.0;

        return (float)(a0 * c0 + a1 * c1 + a2 * c2 + a3 * c3);
    }

    /// <summary>
    /// Отсчёт кольца истории. Индекс 0 — начало текущего кадра, отрицательные
    /// уходят назад по кольцу; за границей кольца индекс физически недостижим
    /// (<see cref="HistorySamples"/> заметно больше <see cref="MaxDryDelay"/>),
    /// но ветки всё равно закрыты — индекс всегда должен остаться в массиве.
    /// </summary>
    private float Sample(float[] hist, int idx)
    {
        int p = _histHead + idx;
        if (p < 0) p += HistorySamples;
        else if (p >= HistorySamples) p -= HistorySamples;
        return hist[p];
    }

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

        // Максимум корреляции должен лежать ВНУТРИ окна поиска. Если он уехал
        // за границу, задержка на самом деле за пределами [MinDryDelay,
        // MaxDryDelay], и подстройка будет бесконечно ползти к границе, а не
        // искать: ровно так выравнивание раньше намертво залипало на 280
        // сэмплах вместо настоящих 960, и кросфейд гасил сигнал в противофазе.
        // Лучше остаться на номинале, который теперь верен, чем упереться в
        // край окна.
        if (c0 < cm || c0 < cp) return;

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
            sum += DelayedL(i, delay) * _rnL[i];
        return sum;
    }

    /// <summary>
    /// Запоминает цели EQ. Ничего не пересчитывает: пересчёт идёт в
    /// <see cref="AdvanceEq"/> раз в кадр, сглаженно.
    /// </summary>
    private void UpdateEq(float lowDb, float midDb, float highDb, float groupDb)
    {
        _targetLowDb = lowDb;
        _targetMidDb = midDb;
        _targetHighDb = highDb;
        _targetGroupDb = groupDb;

        if (_eqPrimed) return;

        // Первичная установка — сразу и точно: состояние фильтров всё равно
        // нулевое (конвейер только что сброшен), так что сглаживать нечего, а
        // разгон от нуля добавил бы к началу работы лишние полсекунды.
        ApplyEq(lowDb, midDb, highDb, groupDb);
        _eqPrimed = true;
    }

    /// <summary>
    /// Двигает EQ к цели, по одному шагу на кадр (10 мс).
    ///
    /// Раньше коэффициенты пересчитывались один раз на пакет и каждый раз с
    /// <see cref="BiquadFilter.ResetState"/>: обнулённое состояние биквада при
    /// ненулевом сигнале на входе — это разрыв, то есть щелчок. И не один, а
    /// сразу шесть, раз в 10..100 мс, на всё время, пока крутилка движется.
    /// Теперь коэффициенты едут к цели плавно, нить фильтра не прерывается —
    /// ровно так же, как это сделано в эквалайзере стрипа.
    /// </summary>
    private void AdvanceEq()
    {
        float coef = EqSmoothCoef;
        bool moving =
            MathF.Abs(_targetLowDb - _eqLow) > 1e-4f ||
            MathF.Abs(_targetMidDb - _eqMid) > 1e-4f ||
            MathF.Abs(_targetHighDb - _eqHigh) > 1e-4f ||
            MathF.Abs(_targetGroupDb - _groupDb) > 1e-4f;
        if (!moving) return;

        float low = _eqLow + (_targetLowDb - _eqLow) * coef;
        float mid = _eqMid + (_targetMidDb - _eqMid) * coef;
        float high = _eqHigh + (_targetHighDb - _eqHigh) * coef;
        float group = _groupDb + (_targetGroupDb - _groupDb) * coef;
        ApplyEq(low, mid, high, group);
    }

    private void ApplyEq(float lowDb, float midDb, float highDb, float groupDb)
    {
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

        // Makeup тоже едет плавно: скачок множителя на голосе слышен как
        // щелчок не хуже, чем обрыв фильтра.
        float targetMakeup = MathF.Pow(10f, groupDb / 20f);
        _makeup = _eqPrimed ? _makeup + (targetMakeup - _makeup) * EqSmoothCoef : targetMakeup;
    }

    /// <summary>
    /// Пик формантного EQ. БЕЗ сброса состояния: коэффициенты меняются
    /// плавно (см. <see cref="AdvanceEq"/>), и обрывать нить фильтра не на чем.
    /// </summary>
    private static void SetPeak(ref BiquadFilter filter, float freq, float gainDb) =>
        filter.SetPeaking(freq, gainDb, Q, InputSource.SampleRate);

    private static float Sanitize(float x) => float.IsFinite(x) && MathF.Abs(x) >= DenormalFloor ? x : 0f;

    private void ResetQueues()
    {
        _inCount = 0;
        _inHead = 0;
        _outCount = 0;
        _outHead = 0;
        Array.Clear(_histL);
        Array.Clear(_histR);
        _histHead = 0;
    }

    private void ResetPipeline()
    {
        ResetQueues();
        Array.Clear(_dryL);
        Array.Clear(_dryR);
        _eqLowL = _eqMidL = _eqHighL = default;
        _eqLowR = _eqMidR = _eqHighR = default;
        _eqPrimed = false;
        _dryDelay = RnNoiseDelay;
    }

    // Очереди хранят сэмплы ИНТЕРЛИВИНГОМ (L0,R0,L1,R1,...) — в том же виде,
    // в каком приходит и уходит аудио. Поэтому кадр (480 сэмплов L и 480 R)
    // занимает FrameFloats ячеек, а чтение и запись кадра обязаны
    // де-/реинтерливить. Если читать кадр как сплошной блок, в денойзер
    // попадёт «L0,R0,L1,R1,...» — сигнал на удвоенной частоте: RNNoise
    // получит мусор, и подавление шума не заработает.
    //
    // Индексы в двух последних считаются по модулю длины без всякой оговорки
    // про «середину кадра никогда не попадает на конец кольца»: правило
    // MaxPacketFrames % FrameSize == 0 делает это верным всегда, но полагаться
    // на него в копировании — значит заложить исключение по индексу в
    // аудиопотоке. Остаток деления на два дешевле.
    private static void EnqueueFrame(float[] queue, ref int count, ref int head, float[] l, float[] r)
    {
        if (count + FrameFloats > queue.Length) return;
        int len = queue.Length;
        int tail = (head + count) % len;
        for (int i = 0; i < FrameSize; i++)
        {
            int p = tail + i * 2;
            if (p >= len) p -= len;
            queue[p] = l[i];
            queue[p + 1] = r[i];
        }
        count += FrameFloats;
    }

    private static void DequeueFrame(float[] queue, ref int count, ref int head, float[] l, float[] r)
    {
        int len = queue.Length;
        for (int i = 0; i < FrameSize; i++)
        {
            int p = head + i * 2;
            if (p >= len) p -= len;
            l[i] = queue[p];
            r[i] = queue[p + 1];
        }
        head = head + FrameFloats >= len ? 0 : head + FrameFloats;
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
        ReleaseStates();
        GC.SuppressFinalize(this);
    }

    private void ReleaseStates()
    {
        RnNoiseInterop.Destroy(_rnStateL);
        RnNoiseInterop.Destroy(_rnStateR);
        _rnStateL = IntPtr.Zero;
        _rnStateR = IntPtr.Zero;
    }
}
