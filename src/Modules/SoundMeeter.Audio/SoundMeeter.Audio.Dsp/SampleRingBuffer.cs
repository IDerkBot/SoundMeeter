using System.Runtime.CompilerServices;

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
/// направлен вход, держит свой курсор.
///
/// ЗДЕСЬ НЕТ СОБСТВЕННОГО ЧАСОВ, И ЭТО СОЗНАТЕЛЬНО.
/// Курсор не «крутит» буфер — он отдаёт столько сэмплов, сколько запросила
/// шина, то есть двигается с той же скоростью, что и воспроизведение. Отсюда
/// важное свойство: кольцо не может накопить задержку. Оно либо отдаёт свежее,
/// либо (когда источник отстал) отдаёт тишину, либо (когда читатель отстал
/// больше ёмкости) перешагивает через затёртое и считает это в
/// <see cref="SkippedFrames"/>. Скорость целиком задаёт выходное устройство,
/// и единственное, чем можно на неё повлиять, — глубина его буфера
/// (см. <see cref="AudioEngineDefaults"/>).
/// </summary>
public sealed class RingCursor
{
    private readonly SampleRingBuffer _ring;
    private readonly int _channels;
    private long _nextRead;
    private long _skippedFrames;

    /// <summary>
    /// Курсор встаёт на ЛИВУЮ границу кольца, а не на его начало.
    ///
    /// Почему это важно. <c>_nextRead</c> — позиция в глобальной шкале кольца,
    /// где 0 — это момент создания <see cref="SampleRingBuffer"/>. У кольца,
    /// которое писало и переполнилось хотя бы раз, <c>written</c> давно больше
    /// ёмкости. Наивный <c>_nextRead = 0</c> в <see cref="Read"/> даёт
    /// <c>start = max(0, written - Capacity)</c>, то есть курсор молча
    /// начинает с <b>целой секунды устаревшего звука</b>: отдаёт её как
    /// актуальную и навсегда остаётся на секунду позади источника.
    ///
    /// Именно это и было видно в журнале: у входа Music кольцо на 430 мс,
    /// при этом у того же стрипа в другой шине — 20…30 мс, потому что там
    /// курсор был создан раньше, когда кольцо ещё не переполнилось.
    ///
    /// Смысл тапа — «звук, который снимается сейчас», а не «звук, который
    /// копился, пока полоса была молча замкнута». Поэтому новый курсор
    /// начинает там, где остановился писатель: первые чтения вернут тишину
    /// (писатель ещё не успел), а дальше курсор идёт с ним в ногу.
    /// </summary>
    internal RingCursor(SampleRingBuffer ring)
    {
        _ring = ring;
        _channels = ring.Capacity % 2 == 0 ? 2 : 1;

        // Под тем же замком, что и Write: иначе между чтением позиции и первым
        // чтением данных писатель успеет записать, и мы потеряем этот пакет.
        lock (_ring.Sync)
        {
            _nextRead = _ring.TotalWritten;
        }
    }

    /// <summary>
    /// Сколько сэмплов пришлось перешагнуть, потому что они были затёрты в кольце
    /// до того, как курсор до них дошёл. Ненулевое значение — это уже потеря
    /// сигнала (а не задержка): в звуке это щелчок или короткий пропуск.
    /// </summary>
    public long SkippedFrames => Interlocked.Read(ref _skippedFrames);

    /// <summary>
    /// Сколько кадров сейчас лежит в кольце непрочитанным, по каждому каналу.
    /// Это и есть текущая задержка этого курсора: значение должно быть меньше
    /// размера кольца и не расти со временем.
    /// </summary>
    public int BufferedFrames
    {
        get
        {
            lock (_ring.Sync)
                return (int)(Math.Max(0, Math.Min(_ring.Capacity, _ring.TotalWritten - _nextRead)) / _channels);
        }
    }

    /// <summary>
    /// Копирует до <paramref name="count"/> свежих сэмплов в <paramref name="dst"/>.
    /// Если данных ещё нет — возвращает 0 (вызывающий заполняет тишиной).
    /// Читатель, отставший больше чем на размер буфера, просто пропускает потерянные сэмплы.
    /// </summary>
    public int Read(float[] dst, int count)
    {
        if (count <= 0) return 0;

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

            // Отставший читатель не копит долг: он перешагивает через затёртое,
            // иначе задержка росла бы после каждого сбоя навсегда. Потеря
            // считается — «пропал звук на стрипе» должно чем-то объясняться.
            long skipped = start - _nextRead;
            if (skipped > 0)
                Interlocked.Add(ref _skippedFrames, skipped / _channels);

            int toRead = (int)Math.Min(count, available);
            CopyFromRing(_ring.Buffer, (int)(start % _ring.Capacity), dst, toRead);

            _nextRead = start + toRead;
            return toRead;
        }
    }

    /// <summary>
    /// Копирует <paramref name="count"/> сэмплов из кольца, начиная с позиции
    /// <paramref name="ringPos"/>. Кольцо замкнуто, поэтому участок разбит
    /// максимум на два отрезка. Раньше здесь стоял <c>% Capacity</c> на каждый
    /// сэмпл — аппаратный <c>idiv</c> на аудиопотоке рендера.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyFromRing(float[] ring, int ringPos, float[] dst, int count)
    {
        int capacity = _ring.Capacity;
        int head = Math.Min(count, capacity - ringPos);
        Array.Copy(ring, ringPos, dst, 0, head);
        if (head < count)
            Array.Copy(ring, 0, dst, head, count - head);
    }
}