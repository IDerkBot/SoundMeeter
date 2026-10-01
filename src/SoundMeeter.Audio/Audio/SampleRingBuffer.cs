namespace SoundMeeter.Audio;

/// <summary>
/// Кольцевой буфер на float-сэмплы с поддержкой нескольких независимых читателей.
/// Один источник (InputSource) пишет в буфер, а каждая шина читает из него
/// через собственный курсор (RingCursor) — так один вход можно направлять
/// в несколько выходов одновременно.
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private long _totalWritten;
    private readonly object _sync = new();

    public SampleRingBuffer(int capacitySamples)
    {
        _capacity = capacitySamples;
        _buffer = new float[capacitySamples];
    }

    public int Capacity => _capacity;

    internal float[] Buffer => _buffer;
    internal object Sync => _sync;
    internal long TotalWritten => _totalWritten;

    /// <summary>
    /// Записывает новые сэмплы, перезаписывая самые старые при переполнении.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        lock (_sync)
        {
            int toCopy = samples.Length;
            int wpos = (int)(_totalWritten % _capacity);
            int copied = 0;

            while (copied < toCopy)
            {
                int chunk = Math.Min(toCopy - copied, _capacity - wpos);
                samples.Slice(copied, chunk).CopyTo(_buffer.AsSpan(wpos, chunk));
                wpos = (wpos + chunk) % _capacity;
                copied += chunk;
            }

            _totalWritten += toCopy;
        }
    }

    public RingCursor OpenCursor() => new(this);
}

/// <summary>
/// Курсор чтения из <see cref="SampleRingBuffer"/>. Каждая шина, в которую
/// направлен вход, держит собственный курсор.
/// </summary>
public sealed class RingCursor
{
    private readonly SampleRingBuffer _ring;
    private long _nextRead;

    internal RingCursor(SampleRingBuffer ring)
    {
        _ring = ring;
    }

    /// <summary>
    /// Копирует до <paramref name="count"/> свежих сэмплов в <paramref name="dst"/>.
    /// Если данных ещё нет — возвращает 0 (вызывающий заполняет тишиной).
    /// Читатель, отставший больше чем на размер буфера, просто пропускает потерянные сэмплы.
    /// </summary>
    public int Read(float[] dst, int count)
    {
        lock (_ring.Sync)
        {
            long written = _ring.TotalWritten;
            long oldest = written - _ring.Capacity;
            long start = Math.Max(_nextRead, oldest);
            long available = written - start;

            if (available <= 0)
            {
                _nextRead = Math.Max(_nextRead, written);
                return 0;
            }

            int toRead = (int)Math.Min(count, available);
            long pos = start % _ring.Capacity;

            for (int i = 0; i < toRead; i++)
            {
                dst[i] = _ring.Buffer[(pos + i) % _ring.Capacity];
            }

            _nextRead = start + toRead;
            return toRead;
        }
    }
}