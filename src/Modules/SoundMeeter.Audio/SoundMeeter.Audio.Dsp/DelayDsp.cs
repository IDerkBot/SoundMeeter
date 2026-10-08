using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Задержка (echo) в цепочке стрипа: insert с регулятором Wet, как и реверберация.
///
/// Тонкость, из-за которой это не «буфер на N сэмплов»: время задержки
/// сглаживается (однополюсный сход ~40 мс). Мгновенная смена времени — это
/// скачок позиции чтения, то есть щелчок в каждом повторе; плавная смена даёт
/// привычное «хвост уезжает».
///
/// В петле обратной связи — однополюсный фильтр (Damping, Гц): без него
/// повторы с быстрым сигналом звонят металлически. Буфер выделяется один раз на
/// максимальное время и не пересоздаётся, поэтому включение задержки не требует
/// пересоздания аудиопотока.
/// </summary>
public sealed class DelayDsp
{
    /// <summary>Максимальное время задержки, мс. Буфер выделяется под него сразу.</summary>
    public const float MaxTimeMs = 2000f;

    /// <summary>Верхняя граница обратной связи, % повторов. Ближе к 100 % хвост
    /// звенит сам по себе и под нагрузкой уходит в самовозбуждение.</summary>
    private const float MaxFeedback = 90f;

    /// <summary>Сглаживание смены времени задержки, мс.</summary>
    private const float TimeSmoothMs = 40f;

    private const float WetRampSamples = 240;   // 5 мс на включение/выключение

    private readonly InputChannelModel _model;
    private readonly DelayLine _left = new(MaxTimeMs);
    private readonly DelayLine _right = new(MaxTimeMs);

    private float _delayLeft, _delayRight;
    private float _ramp = 1f;
    private bool _primed;

    public DelayDsp(InputChannelModel model) => _model = model;

    public void Process(float[] stereo, int frames)
    {
        int samples = frames * 2;
        if (samples > stereo.Length) samples = stereo.Length & ~1;
        if (samples <= 0) return;

        float timeMs = CompressorDsp.Clamp(_model.DelayTimeMs, 1f, MaxTimeMs);
        float feedback = CompressorDsp.Clamp(_model.DelayFeedback, 0f, MaxFeedback) / 100f;
        float wet = CompressorDsp.Clamp(_model.DelayMix, 0f, 100f) / 100f;
        float dampingHz = CompressorDsp.Clamp(_model.DelayDampingHz, 200f, 18000f);
        float dampCoef = 1f - MathF.Exp(-2f * MathF.PI * dampingHz / InputSource.SampleRate);
        float timeCoef = 1f - 1f * MathF.Exp(-1000f / (TimeSmoothMs * InputSource.SampleRate));

        float targetSamples = timeMs * InputSource.SampleRate / 1000f;
        float rampStep = 1f / WetRampSamples;
        float rampTarget = _model.DelayEnabled ? 1f : 0f;
        if (!_primed)
        {
            _ramp = rampTarget;
            _delayLeft = _delayRight = targetSamples;
            _primed = true;
        }

        for (int i = 0; i < samples; i += 2)
        {
            _ramp = _ramp < rampTarget ? MathF.Min(_ramp + rampStep, rampTarget)
                                       : MathF.Max(_ramp - rampStep, rampTarget);
            _delayLeft += (targetSamples - _delayLeft) * timeCoef;
            _delayRight += (targetSamples - _delayRight) * timeCoef;

            float delayedLeft = _left.Process(stereo[i], _delayLeft, feedback, dampCoef);
            float delayedRight = _right.Process(stereo[i + 1], _delayRight, feedback, dampCoef);

            // Wet с учётом включения: выключенная задержка даёт ровно сухой
            // сигнал (mix умножается на рампу), включённая — сухой + мокрый.
            float m = wet * _ramp;
            stereo[i] += delayedLeft * m;
            stereo[i + 1] += delayedRight * m;
        }
    }

    /// <summary>Сбрасывает линию и хвост: новая задержка не должна «подхватывать»
    /// старое эхо, иначе после остановки источника первый пакет хвостает.</summary>
    public void Reset()
    {
        _left.Clear();
        _right.Clear();
        _ramp = 0f;
        _primed = false;
    }

    /// <summary>Кольцевая линия задержки на один канал с однополюсным фильтром
    /// в петле обратной связи.</summary>
    private sealed class DelayLine
    {
        private readonly int _capacity;
        private readonly float[] _buffer;
        private int _size;
        private int _index;
        private float _lowPass;

        public DelayLine(float maxMs)
        {
            _capacity = (int)(maxMs * InputSource.SampleRate / 1000f) + 2;
            _buffer = new float[_capacity];
            _size = _capacity;
        }

        public float Process(float input, float delaySamples, float feedback, float dampCoef)
        {
            int delay = (int)delaySamples;
            if (delay < 0) delay = 0;
            if (delay >= _size) delay = _size - 1;

            int readIndex = _index - delay;
            if (readIndex < 0) readIndex += _size;

            float delayed = _buffer[readIndex];

            // Фильтр в петле: повтор гасится по верхам и не звенит.
            float tail = delayed * feedback;
            _lowPass += (tail - _lowPass) * dampCoef;

            _buffer[_index] = input + _lowPass;
            _index++;
            if (_index >= _size) _index = 0;

            return delayed;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _index = 0;
            _lowPass = 0f;
        }
    }
}
