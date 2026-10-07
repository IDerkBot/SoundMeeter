using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Audio;

/// <summary>
/// Очередь внешнего (подмешиваемого) звука стрипа — источник синтеза речи.
///
/// Кольцом такую очередь быть НЕ может, и это не вопрос реализации, а арифметики.
/// У стрипа с устройством кольцо уже занято: loopback-захват кладёт в него ровно
/// столько, сколько шина забирает, — 48 кГц в секунду в секунду. Свободной полосы
/// нет. Добавить туда ещё 48 кГц (синтез речи отдаётся за доли секунды) — значит
/// переполнить кольцо, и читатель перескочит к последней секунде сообщения.
/// Именно это и звучало как «озвучивается только конец».
///
/// Поэтому внешний звук идёт мимо кольца, и забирает его <see cref="BusTap"/> на
/// выходе стрипа. Тап берёт ровно столько, сколько у него попросили, поэтому темп
/// задаёт выходное устройство и совпадает с остальным звуком стрипа по построению:
/// отдельный поток-задающий-темп не нужен вовсе.
///
/// У стрипа без устройства (генерируемого) кольцо пустое, и очередь — единственный
/// источник звука; там она работает точно так же.
///
/// ЧИТАТЕЛЕЙ МОЖЕТ БЫТЬ НЕСКОЛЬКО, И ЭТО НЕ УДОБСТВО, А ТРЕБОВАНИЕ.
///
/// На стрип, направленный в несколько шин, создаётся по тапу на шину, и каждому
/// нужна ПОЛНАЯ копия одной и той же речи: одна фраза на все посылки. С одной
/// общей очередью второй тап забирал бы вторую половину слов, и одна из шин
/// зазвучала бы огрызком. Поэтому здесь своя позиция чтения на каждого читателя,
/// а данные общие и живут, пока их не забрал последний. На нового читателя
/// копия НЕ переигрывается (он встаёт на живую границу, как и
/// <see cref="RingCursor"/>): подключили посылку посреди фразы — услышите её
/// остаток, а не начало заново.
/// </summary>
public sealed class SampleQueue
{
    private const int SampleRate = InputSource.SampleRate;
    private const int Channels = InputSource.Channels;

    /// <summary>
    /// Предел очереди, секунд. Скорость сбора и расхода совпадает, так что в норме
    /// очередь держится пустой, и предел — это страховка от часа: если полоса
    /// перекрыта (микрофон в монополии, кабель не слушают), копить речь незачем.
    /// </summary>
    private const int MaxSeconds = 30;

    private static readonly int MaxSamples = SampleRate * Channels * MaxSeconds;

    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly string _stripName;
    private readonly List<Reader> _readers = new();

    private float[] _buffer = new float[SampleRate * Channels / 10];
    private int _count;                  // сколько всего записано (с начала жизни)
    private bool _warnedOverflow;

    public SampleQueue(string stripName)
    {
        _stripName = stripName;
        _logger = AppLog.For<SampleQueue>();
    }

    /// <summary>
    /// Сколько секунд ждёт выдачи. Считается по самому отставшему читателю: если
    /// хоть одна шина ещё не забрала звук, фраза для модуля речи не закончилась.
    /// Когда читателей нет (стрип никому не направлен), берётся весь объём.
    /// </summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_sync) return (_count - OldestReadLocked()) / (double)(SampleRate * Channels);
        }
    }

    /// <summary>Есть ли кому отдавать. Если читателей нет, копить незачем.</summary>
    public bool HasReaders
    {
        get
        {
            lock (_sync) return _readers.Count > 0;
        }
    }

    /// <summary>Кладёт звук в очередь. Вызывается из потока синтеза, не блокирует.</summary>
    public void Enqueue(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        lock (_sync)
        {
            if (_readers.Count == 0) return;   // никто не слушает — не копим в пустоту

            int count = samples.Length;
            if (_count + count - OldestReadLocked() > MaxSamples)
            {
                WarnOverflowLocked();
                return;
            }

            EnsureRoomLocked(count);
            samples.CopyTo(_buffer.AsSpan(_count, count));
            _count += count;
        }
    }

    /// <summary>
    /// Выдаёт читателю до <paramref name="count"/> сэмплов и возвращает, сколько
    /// реально досталось. Ноль означает «данных нет», а не «не ждать»: тап
    /// подставит тишину и двинется дальше.
    /// </summary>
    internal int Read(Reader reader, float[] dst, int count)
    {
        if (count <= 0) return 0;

        lock (_sync)
        {
            int available = _count - reader.Position;
            if (available <= 0) return 0;

            int take = Math.Min(count, available);
            Array.Copy(_buffer, reader.Position, dst, 0, take);
            reader.Position += take;

            CompactLocked();
            return take;
        }
    }

    /// <summary>Выбросить всё непрочитанное (кнопка «стоп»).</summary>
    public bool Clear()
    {
        lock (_sync)
        {
            bool had = _count > OldestReadLocked();
            foreach (var reader in _readers) reader.Position = _count;
            CompactLocked();
            return had;
        }
    }

    /// <summary>
    /// Читатель для одного тапа. Позиция приватная, поэтому N тапов видят одну и ту
    /// же речь целиком, а не разделённые куски.
    /// </summary>
    public sealed class Reader
    {
        internal Reader(SampleQueue owner) => Owner = owner;

        /// <summary>Очередь, выдавшая этого читателя.</summary>
        public SampleQueue Owner { get; }

        internal int Position { get; set; }

        /// <summary>Читает темпом шины. См. <see cref="SampleQueue.Read"/>.</summary>
        public int Read(float[] dst, int count) => Owner.Read(this, dst, count);
    }

    /// <summary>
    /// Создаёт читателя, встав на текущую границу.
    ///
    /// Порядок обязателен: читатель берётся ДО <see cref="Enqueue"/>. Очередь не
    /// переигрывает новому читателю накопленное — он встаёт на живую границу, как и
    /// <see cref="RingCursor"/>. Иначе подключение посылки посреди фразы начинало
    /// бы звучать с её начала заново.
    /// </summary>
    public Reader OpenReader()
    {
        lock (_sync) return CreateReaderLocked();
    }

    /// <summary>Забывает читателя. Его позиция больше не держит данные в очереди.</summary>
    public void CloseReader(Reader reader)
    {
        lock (_sync)
        {
            if (_readers.Remove(reader)) CompactLocked();
        }
    }

    private Reader CreateReaderLocked()
    {
        var reader = new Reader(this) { Position = _count };
        _readers.Add(reader);
        return reader;
    }

    private int OldestReadLocked()
    {
        int oldest = _count;
        foreach (var reader in _readers)
            if (reader.Position < oldest) oldest = reader.Position;
        return oldest;
    }

    /// <summary>
    /// Сдвигает буфер, если всё прочитано. Медленного читателя НЕ выбрасываем
    /// молча: стирание его данных на середине фразы дало бы щелчок, лучше пусть он
    /// дочитает своё.
    /// </summary>
    private void CompactLocked()
    {
        int oldest = OldestReadLocked();
        if (oldest <= 0) return;

        int live = _count - oldest;
        if (live == 0)
        {
            _count = 0;
            foreach (var reader in _readers) reader.Position = 0;
            return;
        }

        Array.Copy(_buffer, oldest, _buffer, 0, live);
        _count = live;
        foreach (var reader in _readers) reader.Position -= oldest;
    }

    private void EnsureRoomLocked(int count)
    {
        int oldest = OldestReadLocked();
        int live = _count - oldest;

        if (live + count <= _buffer.Length)
        {
            if (oldest > 0) ShiftToStartLocked(oldest, live);
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < live + count) capacity *= 2;

        var grown = new float[capacity];
        Array.Copy(_buffer, oldest, grown, 0, live);
        _buffer = grown;
        ShiftToStartLocked(oldest, live);
    }

    /// <summary>Переносит непрочитанное в начало буфера и пересчитывает позиции.</summary>
    private void ShiftToStartLocked(int oldest, int live)
    {
        if (oldest <= 0) return;

        Array.Copy(_buffer, oldest, _buffer, 0, live);
        _count = live;
        foreach (var reader in _readers) reader.Position = Math.Max(0, reader.Position - oldest);
    }

    private void WarnOverflowLocked()
    {
        if (_warnedOverflow) return;

        _warnedOverflow = true;
        _logger.LogWarning(
            "Strip «{Strip}»: очередь подмешиваемого звука переполнилась ({Seconds} с) — " +
            "новые данные отбрасываются. Обычно это значит, что канал никто не слушает",
            _stripName, MaxSeconds);
    }
}