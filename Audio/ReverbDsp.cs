using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Реверберация в цепочке стрипа: insert с регулятором Wet, алгоритмическая
/// (Freeverb) — без ИХ-файлов, поэтому нечего терять при обновлении сборки и
/// не надо грузить диск на запуте.
///
/// Устройство: 8 гребёнок (comb) и 4 всепропускающих (allpass) фильтра на канал.
/// Гребёнки задают реверберационную плотность и время, всепропускающие —
/// рассеивание ранних отражений. Длина буферов взята из оригинальной настройки
/// Freeverb для 44,1 кГц и пересчитана на 48 кГц, иначе хвост был бы короче
/// заявленного примерно на 8 %.
///
/// Ограничения, о которых стоит помнить: буферы занимают ~110 КБ на стрип
/// (выделяются сразу), Wet и размер слышно только при включённом блоке, а
/// медленная атака (около 20 мс) означает, что реверберация слышна на
/// инструментах и голосе, но почти не слышна на отдельных щелчках.
/// </summary>
public sealed class ReverbDsp
{
    private const int CombCount = 8;
    private const int AllpassCount = 4;

    /// <summary>Длины гребёнок Freeverb для 44,1 кГц, сэмплов.</summary>
    private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };

    /// <summary>Длины всепропускающих фильтров Freeverb для 44,1 кГц, сэмплов.</summary>
    private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };

    /// <summary>Разброс гребёнок между каналами, сэмплов: без него реверберация
    /// схлопывается в моно и звучит как бетонная труба.</summary>
    private const int StereoSpread = 23;

    /// <summary>Обратная связь гребёнок: 0.72 (комната) .. 0.95 (зал). Дальше
    /// хвост самовозбуждается.</summary>
    private const float MinCombFeedback = 0.72f;
    private const float MaxCombFeedback = 0.95f;

    /// <summary>Обратная связь всепропускающих (оригинальное значение Freeverb).</summary>
    private const float AllpassFeedback = 0.5f;

    /// <summary>
    /// Фиксированное усиление входа Freeverb. Оно обязательно: у гребёнки на низких
    /// частотах усиление 1/(1 − feedback) — до +20 дБ, и сумма восьми параллельных
    /// фильтров на импульсе разгоняется так, что даже 1/8 от суммы не спасает —
    /// реверберация «взрывается» и упирается в ограничитель. Масштаб входа это
    /// гасит, а на слух реверберация остаётся вровень с сухим сигналом.
    /// </summary>
    private const float InputGain = 0.015f;

    private const float WetRampSamples = 240;   // 5 мс на включение/выключение

    private readonly InputChannelModel _model;
    private readonly Comb[] _combLeft = new Comb[CombCount];
    private readonly Comb[] _combRight = new Comb[CombCount];
    private readonly Allpass[] _allpassLeft = new Allpass[AllpassCount];
    private readonly Allpass[] _allpassRight = new Allpass[AllpassCount];

    private float _ramp;
    private bool _primed;

    public ReverbDsp(InputChannelModel model)
    {
        _model = model;

        // 44,1 кГц -> 48 кГц: без пересчёта хвост короче заявленного размера.
        float scale = InputSource.SampleRate / 44100f;
        for (int i = 0; i < CombCount; i++)
        {
            _combLeft[i] = new Comb((int)(CombTuning[i] * scale));
            _combRight[i] = new Comb((int)(CombTuning[i] * scale) + StereoSpread);
        }

        for (int i = 0; i < AllpassCount; i++)
        {
            _allpassLeft[i] = new Allpass((int)(AllpassTuning[i] * scale));
            _allpassRight[i] = new Allpass((int)(AllpassTuning[i] * scale) + StereoSpread);
        }
    }

    public void Process(float[] stereo, int frames)
    {
        int samples = frames * 2;
        if (samples > stereo.Length) samples = stereo.Length & ~1;
        if (samples <= 0) return;

        float size = CompressorDsp.Clamp(_model.ReverbSize, 0f, 100f) / 100f;
        float damping = CompressorDsp.Clamp(_model.ReverbDamping, 0f, 100f) / 100f;
        float wet = CompressorDsp.Clamp(_model.ReverbMix, 0f, 100f) / 100f;

        float combFeedback = MinCombFeedback + (MaxCombFeedback - MinCombFeedback) * size;
        float dampKeep = 1f - damping;      // 0 — гасим верх в петле полностью
        float rampStep = 1f / WetRampSamples;
        float rampTarget = _model.ReverbEnabled ? 1f : 0f;
        if (!_primed)
        {
            _ramp = rampTarget;
            _primed = true;
        }

        // Сумма восьми гребёнок на единицу входа: без нормировки восемь
        // параллельных фильтров дают выигрыш до +18 дБ, и реверберация
        // «взрывается» даже при умеренном Wet.
        float norm = 1f / CombCount;

        for (int i = 0; i < samples; i += 2)
        {
            _ramp = _ramp < rampTarget ? MathF.Min(_ramp + rampStep, rampTarget)
                                       : MathF.Max(_ramp - rampStep, rampTarget);

            float inputLeft = stereo[i];
            float inputRight = stereo[i + 1];

            // Freeverb смешивает каналы в моно перед сетью фильтров: так реверберация
            // не «гуляет» по стерео и не схлопывается в раздельные хвосты.
            float feed = (inputLeft + inputRight) * 0.5f * InputGain;

            float wetLeft = 0f;
            for (int c = 0; c < CombCount; c++) wetLeft += _combLeft[c].Process(feed, combFeedback, dampKeep);
            wetLeft *= norm;

            float wetRight = 0f;
            for (int c = 0; c < CombCount; c++) wetRight += _combRight[c].Process(feed, combFeedback, dampKeep);
            wetRight *= norm;

            for (int a = 0; a < AllpassCount; a++)
            {
                wetLeft = _allpassLeft[a].Process(wetLeft);
                wetRight = _allpassRight[a].Process(wetRight);
            }

            float m = wet * _ramp;
            stereo[i] = inputLeft + wetLeft * m;
            stereo[i + 1] = inputRight + wetRight * m;
        }
    }

    /// <summary>Гасит хвост: при остановке источника иначе реверберация ещё
    /// секунду звучит в кольцевой буфер, откуда её услышат уже другие посылки.</summary>
    public void Reset()
    {
        foreach (var comb in _combLeft) comb.Clear();
        foreach (var comb in _combRight) comb.Clear();
        foreach (var filter in _allpassLeft) filter.Clear();
        foreach (var filter in _allpassRight) filter.Clear();
        _ramp = 0f;
        _primed = false;
    }

    /// <summary>Гребёнка с однополюсным гашением верхов в петле обратной связи.</summary>
    private sealed class Comb
    {
        private readonly float[] _buffer;
        private int _index;
        private float _store;

        public Comb(int size) => _buffer = new float[Math.Max(4, size)];

        /// <summary>
        /// Гребёнка Freeverb. Фильтр в петле сделан усреднением двух отсчётов
        /// (damp1/damp2 из оригинала), а не однополюсным: он устойчивее при
        /// большой обратной связи и гасит хвост по верхам мягче.
        /// </summary>
        public float Process(float input, float feedback, float dampKeep)
        {
            float output = _buffer[_index];
            float damped = output * dampKeep + _store * (1f - dampKeep);
            _store = (output * (1f - dampKeep) + damped * dampKeep) * feedback;
            _buffer[_index] = input + _store;
            if (++_index >= _buffer.Length) _index = 0;
            return output;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _store = 0f;
            _index = 0;
        }
    }

    /// <summary>Всепропускающий фильтр: рассеивает ранние отражения.</summary>
    private sealed class Allpass
    {
        private readonly float[] _buffer;
        private int _index;

        public Allpass(int size) => _buffer = new float[Math.Max(4, size)];

        public float Process(float input)
        {
            float buffered = _buffer[_index];
            float output = -input + buffered;
            _buffer[_index] = input + buffered * AllpassFeedback;
            if (++_index >= _buffer.Length) _index = 0;
            return output;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _index = 0;
        }
    }
}
