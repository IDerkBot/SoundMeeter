namespace SoundMeeter.Audio;

/// <summary>
/// Потоковый ресемплер с полосой ограничения: интерполяция оконной sinc-функцией
/// с децимацией в одном шаге (полифазный фильтр).
///
/// <para><b>Зачем.</b> Раньше любой источник приводился к 48 кГц линейной
/// интерполяцией. Линейная интерполяция — фильтр со срезом на четверти Найквиста:
/// при понижении частоты (96 → 48 кГц) всё выше 12 кГц не подавляется, а
/// складывается обратно в слышимый диапазон. На слух это «металлический» тембр,
/// на спектрограмме — зеркальные составляющие. Нормальный ресемплер обязан обрезать
/// спектр по НИЖНЕЙ из двух частот Найквиста, иначе децимация неизбежно складывает его.</para>
///
/// <para><b>Как считается.</b> Отношение частот сводится к рациональному
/// <c>up/down</c> (НОК частот), поэтому положение выходного отсчёта относительно
/// входных считается целочисленно: <c>i0 = n * down / up</c>. Погрешность за сутки
/// работы не накапливается by construction — это и есть требование «без накопления
/// дрейфа длины буфера».</para>
///
/// <para><b>Почему не MediaFoundationResampler.</b> MF-ресемплер тянет
/// <c>IWaveProvider</c> (pull-модель) и поднимает Media Foundation прямо в потоке
/// обработки аудиопакета, где любая заминка слышна как щелчок. Здесь push-модель
/// (подал вход — получил выход), состояние предсказуемо, а качество проверяется
/// расчётом, а не на слух.</para>
///
/// <para>Длина выхода за пакет считается накопительно: <c>target = totalIn *
/// targetRate / sourceRate</c>. Вход, которого не хватает для очередного выходного
/// кадра, остаётся в буфере и доберётся в следующем пакете — сигнал никогда не
/// растягивается и не дублируется.</para>
/// </summary>
public sealed class PolyphaseResampler
{
    /// <summary>Полуокно прототипа sinc в нулевых пересечениях.</summary>
    private const int HalfZeros = 32;

    /// <summary>Сколько секунд выхода допускаем копить при переполнении очереди.</summary>
    private const int MaxQueuedOutputSeconds = 1;

    private readonly int _channels;
    private readonly int _sourceRate;
    private readonly int _targetRate;

    /// <summary>Числитель рационального отношения (up = targetRate / НОД).</summary>
    private readonly int _up;

    /// <summary>Знаменатель рационального отношения (down = sourceRate / НОД).</summary>
    private readonly int _down;

    /// <summary>Срез фильтра: удвоенная частота среза идеального sinc, циклов на отсчёт входа.</summary>
    private readonly float _cutoff;

    /// <summary>Полуокно фильтра во входных отсчётах: HalfZeros / cutoff.</summary>
    private readonly int _half;

    /// <summary>Кэш коэффициентов по фазам (дробная часть положения), ленивый.</summary>
    private readonly float[]?[] _phaseKernels;

    /// <summary>Линейный буфер входных кадров: кадр <c>bufFirst</c> лежит с индекса 0.</summary>
    private float[] _buffer;
    private long _bufFirst;
    private int _bufCount;

    /// <summary>Кольцевая очередь готовых выходных кадров.</summary>
    private readonly float[] _outQueue;
    private readonly int _outQueueFrames;
    private int _outCount;
    private int _outHead;

    private long _totalIn;
    private long _produced;
    private long _emitted;

    public PolyphaseResampler(int sourceRate, int targetRate, int channels = 2)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _channels = channels;
        _sourceRate = sourceRate;
        _targetRate = targetRate;

        var g = Gcd(sourceRate, targetRate);
        _up = targetRate / g;
        _down = sourceRate / g;

        // Частота среза идеального ядра sinc(с·u) равна c/2 цикла на отсчёт,
        // поэтому здесь стоит удвоенная частота Найквиста НИЖНЕЙ из двух сторон.
        // Без этого ограничения при децимации спектр складывается обратно в
        // слышимый диапазон: 40 кГц на входе 96 кГц превратились бы в 8 кГц.
        _cutoff = MathF.Min(1f, (float)targetRate / sourceRate);
        _half = (int)MathF.Ceiling(HalfZeros / _cutoff);
        var taps = 2 * _half + 1;

        // Буфер входа должен вмещать окно фильтра плюс пакет с запасом.
        _buffer = new float[Math.Max(2 * _half + 64, 4096) * _channels];
        _outQueueFrames = MaxQueuedOutputSeconds * targetRate + 2;
        _outQueue = new float[_outQueueFrames * _channels];
        // Число различных фаз равно числителю отношения: дробная часть позиции
        // n * down / up принимает ровно up различных значений.
        _phaseKernels = new float[Math.Max(_up, 1)][];

        Taps = taps;
    }

    public int SourceRate => _sourceRate;
    public int TargetRate => _targetRate;
    public int Channels => _channels;

    /// <summary>Отношение вход/выход (например, 2.0 для 96 → 48 кГц).</summary>
    public double Ratio => (double)_sourceRate / _targetRate;

    /// <summary>Число отсчётов в фильтре — диагностика качества ресемплера.</summary>
    public int Taps { get; }

    /// <summary>Выходных кадры, накопленные, но ещё не забранные вызывающим кодом.</summary>
    public int PendingOutputFrames => _outCount;

    /// <summary>
    /// Кладёт <paramref name="inputFrames"/> входных кадров в поток и забирает
    /// до <paramref name="maxOutputFrames"/> выходных. Возвращает число записанных
    /// кадров: 0 — выход ещё не «дозрел» (вход накоплен, кадры появятся в следующем
    /// вызове) либо выбранный размер буфера уже выдан.
    /// </summary>
    public int Process(float[] input, int inputFrames, float[] output, int maxOutputFrames)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (inputFrames <= 0 || maxOutputFrames <= 0) return 0;

        if (_sourceRate == _targetRate)
        {
            var copy = Math.Min(inputFrames, maxOutputFrames);
            Array.Copy(input, 0, output, 0, copy * _channels);
            return copy;
        }

        AppendInput(input, inputFrames);

        // Сколько выходных кадров «должно» быть к текущему моменту — точный
        // рациональный счёт, без накопления ошибки округления.
        var target = _totalIn * _targetRate / _sourceRate;
        while (_produced < target)
        {
            var n = _produced;
            // Положение выходного отсчёта n во входных координатах: n * L / M,
            // то есть n * down / up. Внизу окна фильтра раньше накапливается вход.
            var i0 = n * _down / _up;

            // Окно фильтра должно целиком лежать в буфере; пока это не так,
            // ждём следующий пакет входа.
            if (i0 + _half >= _bufFirst + _bufCount) break;

            WriteOutputFrame(i0, GetKernel((int)(n * _down % _up)));

            // Начало окна следующего отсчёта строго правее: старое больше не нужно.
            Trim(i0 - _half);
            _produced++;
        }

        return Drain(output, maxOutputFrames);
    }

    /// <summary>Сбрасывает состояние (новое устройство, новый формат, переподключение).</summary>
    public void Reset()
    {
        _bufFirst = 0;
        _bufCount = 0;
        _outCount = 0;
        _outHead = 0;
        _totalIn = 0;
        _produced = 0;
        _emitted = 0;
    }

    private void AppendInput(float[] input, int frames)
    {
        EnsureBufferCapacity(frames);

        // Буфер линейный: кадр bufFirst всегда в начале, новые кадры пишем в хвост.
        Array.Copy(input, 0, _buffer, _bufCount * _channels, frames * _channels);
        _bufCount += frames;
        _totalIn += frames;
    }

    private void EnsureBufferCapacity(int incomingFrames)
    {
        if ((_bufCount + incomingFrames) * _channels <= _buffer.Length) return;

        // Сначала пробуем обойтись компактизацией: окно фильтра маленькое,
        // поэтому реальный буфер почти никогда не растёт.
        Trim(_bufFirst + _bufCount - (2 * _half + 1));
        if ((_bufCount + incomingFrames) * _channels <= _buffer.Length) return;

        var grown = new float[Math.Max((_bufCount + incomingFrames) * _channels, _buffer.Length * 2)];
        Array.Copy(_buffer, 0, grown, 0, _bufCount * _channels);
        _buffer = grown;
    }

    /// <summary>Выбрасывает кадры с абсолютными индексами меньше <paramref name="keepFrom"/>.</summary>
    private void Trim(long keepFrom)
    {
        var drop = keepFrom - _bufFirst;
        if (drop <= 0) return;
        if (drop >= _bufCount)
        {
            _bufFirst += _bufCount;
            _bufCount = 0;
            return;
        }

        Array.Copy(_buffer, (int)drop * _channels, _buffer, 0, (_bufCount - (int)drop) * _channels);
        _bufCount -= (int)drop;
        _bufFirst += drop;
    }

    private void WriteOutputFrame(long i0, float[] kernel)
    {
        var dst = ReserveOutputFrame();

        int baseIndex = (int)(i0 - _bufFirst);
        int validLo = Math.Max(-_half, -baseIndex);
        int validHi = Math.Min(_half, _bufCount - 1 - baseIndex);

        if (validLo > validHi)
        {
            dst.Clear();
            return;
        }

        int firstSample = (baseIndex + validLo) * _channels;
        int lastSample = (baseIndex + validHi) * _channels;
        for (int ch = 0; ch < _channels; ch++)
        {
            float sum = 0f;
            int k = validLo + _half;
            for (int s = firstSample + ch; s <= lastSample; s += _channels, k++)
                sum += _buffer[s] * kernel[k];
            dst[ch] = float.IsFinite(sum) ? sum : 0f;
        }
    }

    /// <summary>
    /// Место под один выходной кадр в кольцевой очереди. Кольцо уплотняется только
    /// когда кадр не помещается в конец (амортизированная компактизация), а при
    /// полном кольце отбрасывается самое старое — иначе задержка росла бы со
    /// временем, а вызывающий код так и не увидел бы свежих кадров.
    /// </summary>
    private Span<float> ReserveOutputFrame()
    {
        if (_outHead + _outCount + 1 > _outQueueFrames)
        {
            if (_outHead > 0)
            {
                Array.Copy(_outQueue, _outHead * _channels, _outQueue, 0, _outCount * _channels);
                _outHead = 0;
            }

            if (_outCount + 1 > _outQueueFrames)
            {
                int drop = _outCount - _outQueueFrames / 2;
                Array.Copy(_outQueue, (_outHead + drop) * _channels, _outQueue,
                    _outHead * _channels, (_outCount - drop) * _channels);
                _outCount -= drop;
                _emitted += drop;
            }
        }

        var frame = _outQueue.AsSpan((_outHead + _outCount) * _channels, _channels);
        _outCount++;
        return frame;
    }

    private int Drain(float[] output, int maxOutputFrames)
    {
        int take = Math.Min(maxOutputFrames, _outCount);
        if (take <= 0) return 0;

        int first = Math.Min(take, _outQueueFrames - _outHead);
        Array.Copy(_outQueue, _outHead * _channels, output, 0, first * _channels);
        if (first < take)
            Array.Copy(_outQueue, 0, output, first * _channels, (take - first) * _channels);

        _outHead = (_outHead + take) % _outQueueFrames;
        _outCount -= take;
        _emitted += take;
        return take;
    }

    /// <summary>
    /// Коэффициенты фильтра для одной фазы (дробной части положения отсчёта).
    /// Фаза повторяется с периодом <c>up</c>, поэтому sinc считается один раз
    /// на фазу, а не на каждый сэмпл.
    /// </summary>
    private float[] GetKernel(int phase)
    {
        var cached = _phaseKernels[phase];
        if (cached != null) return cached;

        var kernel = new float[Taps];
        double frac = (double)phase / _up;
        double sum = 0.0;

        for (int j = -_half; j <= _half; j++)
        {
            double u = j - frac;
            double value = _cutoff * Sinc(_cutoff * u) * BlackmanHarris(u / _half);
            kernel[j + _half] = (float)value;
            sum += value;
        }

        // Нормировка на конечную сумму окна: для постоянного сигнала на выходе
        // получается ровно тот же уровень, без пульсаций по фазе.
        if (sum > 1e-12)
        {
            float gain = (float)(1.0 / sum);
            for (int i = 0; i < Taps; i++) kernel[i] *= gain;
        }
        else
        {
            Array.Clear(kernel);
            kernel[_half] = 1f;
        }

        _phaseKernels[phase] = kernel;
        return kernel;
    }

    /// <summary>Нормированный sinc: sin(pi*x)/(pi*x), sinc(0) = 1.</summary>
    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        var pix = Math.PI * x;
        return Math.Sin(pix) / pix;
    }

    /// <summary>
    /// Окно Блэкмана-Харриса, растянутое на [-1, 1]: единица в центре, ноль на краях.
    /// Боковые лепестки около −92 дБ — зеркальные составляющие после децимации
    /// не видны ни на слух, ни на спектрограмме.
    /// </summary>
    private static double BlackmanHarris(double x)
    {
        if (x <= -1.0 || x >= 1.0) return 0.0;
        var p = Math.PI * x;
        return 0.35875 + 0.48829 * Math.Cos(p) + 0.14128 * Math.Cos(2 * p) + 0.01168 * Math.Cos(3 * p);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a == 0 ? 1 : a;
    }
}
