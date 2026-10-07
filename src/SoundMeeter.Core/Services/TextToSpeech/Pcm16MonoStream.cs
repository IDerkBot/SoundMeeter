namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Поток, который SAPI считает устройством вывода, а на деле конвертирует
/// PCM16/моно в float/стерео 48 кГц — ровно то, что ждёт кольцо микшера — и
/// отдаёт готовые сэмплы порциями.
///
/// Три вещи, из-за которых это отдельный класс, а не тело
/// <see cref="SapiSpeechEngine"/>:
///
/// <list type="bullet">
/// <item><b>Стыки порций.</b> SAPI режет поток как ему удобно, поэтому и нечётный
///       байт, начавшийся в одном вызове <see cref="Write"/>, и непроизнесённые
///       кадры интерполяции обязаны дожить до следующего. Без этого звук заикался бы
///       щелчками на каждой границе.</item>
/// <item><b>Смещение по времени.</b> Соотношение частот считается от абсолютного
///       номера выходного кадра, а не от позиции внутри текущего блока: иначе
///       ошибка накапливалась бы и через минуту речь поплыла бы по времени.</item>
/// <item><b>Проверяемость.</b> Это единственное место с нетривиальной арифметикой в
///       модуле, и оно проверяется обычными тестами — без SAPI, без звуковой
///       карты и без голосов в системе.</item>
/// </list>
/// </summary>
internal sealed class Pcm16MonoStream : Stream
{
    /// <summary>Частота выдачи. Совпадает с частотой кольца микшера.</summary>
    internal const int TargetRate = 48000;

    /// <summary>
    /// Порция выдачи, кадров (10 мс).
    ///
    /// Это потолок, а не шаг: короткий остаток отдаётся вместе с текущей порцией
    /// записи, а не ждёт следующего вызова. Иначе начало фразы зависело бы от
    /// того, с каким куском SAPI напишет, а он пишет раз в 100–200 мс — на
    /// короткой реплике это превращалось в ощутимую паузу перед первым словом.
    /// </summary>
    internal const int ChunkFrames = TargetRate / 100;

    private readonly int _sourceRate;
    private readonly Action<float[]> _sink;
    private readonly float[] _chunk = new float[ChunkFrames * 2];

    /// <summary>Исходные отсчёты; <c>_mono[0]</c> — это абсолютный индекс <c>_monoStart</c>.</summary>
    private float[] _mono = new float[TargetRate / 10];
    private long _monoStart;
    private int _monoCount;

    /// <summary>Нечётный хвост прошлого вызова — низший байт отсчёта, начавшегося в нём.</summary>
    private byte _pendingByte;
    private bool _hasPendingByte;

    /// <summary>Сколько выходных кадров выдано — от него отсчитывается вся интерполяция.</summary>
    private long _produced;
    private int _chunkFill;

    /// <summary>Сколько байт в него записано. SAPI читает это перед записью.</summary>
    private long _position;

    /// <param name="sourceRate">Частота PCM16, которую отдаёт SAPI.</param>
    /// <param name="sink">Куда отдавать готовые порции float/стерео.</param>
    public Pcm16MonoStream(int sourceRate, Action<float[]> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));

        _sourceRate = sourceRate;
        _sink = sink;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    /// <summary>
    /// Сколько байт записано, а не «длина файла»: поток пишется вперёд и ничего не
    /// хранится на диске.
    ///
    /// Именно поэтому свойство не бросает исключение. SAPI читает <c>Position</c> и
    /// <c>Length</c> у потока вывода при подключении, и <c>NotSupportedException</c>
    /// оттуда прерывал синтез целиком — с ошибкой «SAPI не поддерживает формат»,
    /// хотя поддерживает. Проверяется это в TextToSpeechIntegrationTests.
    /// </summary>
    public override long Length => _position;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        int start = 0;

        if (_hasPendingByte && !buffer.IsEmpty)
        {
            Append(_pendingByte, buffer[0]);
            _hasPendingByte = false;
            start = 1;
        }

        int samples = (buffer.Length - start) / 2;
        for (int i = 0; i < samples; i++)
            Append(buffer[start + i * 2], buffer[start + i * 2 + 1]);

        if (start + samples * 2 < buffer.Length)
        {
            _pendingByte = buffer[^1];
            _hasPendingByte = true;
        }

        _position += buffer.Length;
        Generate();
    }

    private void Append(byte low, byte high)
    {
        if (_monoCount == _mono.Length) Array.Resize(ref _mono, _mono.Length * 2);

        short value = (short)((short)low | (short)(high << 8));
        _mono[_monoCount++] = value / 32768f;
    }

    /// <summary>
    /// Досинтезирует столько кадров, сколько позволяет уже полученный вход.
    /// Линейная интерполяция: при 24 кГц (самый частый случай, если SAPI отказал
    /// от 48) она попадает точно в каждый второй исходный отсчёт, то есть на
    /// речи не поднимает слышимого шума.
    /// </summary>
    private void Generate()
    {
        while (_produced >= 0)
        {
            double position = (double)_produced * _sourceRate / TargetRate;
            long index = (long)Math.Floor(position);

            // Правый сосед ещё не пришёл. Ждать нельзя — SAPI пришлёт данные
            // только в следующем Write, а тишина в этот момент всё равно лучше
            // того, что выдалось бы по догадке.
            if (index + 1 >= _monoStart + _monoCount) break;

            // Отсчёт уже подрезан: вход отстал больше, чем можно ждать.
            // Продолжаем с края, чтобы не зациклиться.
            if (index < _monoStart) index = _monoStart;

            // Отсчёты берутся ВОКРУГ позиции, а не вокруг целой её части:
            // при frac = 0 результат обязан совпасть с исходным отсчётом, иначе
            // на чётных кадрах звучал бы предыдущий — речь сбивалась бы на
            // полшага на каждом стыке.
            float left = _mono[index - _monoStart];
            float right = _mono[index + 1 - _monoStart];

            Emit(left + (right - left) * (float)(position - index));
            _produced++;
        }

        Trim();

        // Остаток отдаём сразу, а не копим до полной порции. SAPI пишет в поток
        // крупными кусками раз в 100–200 мс, и ждать полного 10-миллисекундного
        // блока значило бы добавлять к началу каждой фразы столько же, сколько он
        // и так молчит, прежде чем заговорить.
        FlushChunk();
    }

    /// <summary>
    /// Освобождает начало буфера. Оставляется ровно тот отсчёт, на котором стоит
    /// текущая позиция, — он и есть левый край интерполяции следующего кадра.
    /// </summary>
    private void Trim()
    {
        long keep = Math.Max(_monoStart, (long)Math.Floor((double)_produced * _sourceRate / TargetRate));
        if (keep == _monoStart) return;

        int drop = (int)(keep - _monoStart);
        Array.Copy(_mono, drop, _mono, 0, _monoCount - drop);
        _monoCount -= drop;
        _monoStart = keep;
    }

    private void Emit(float mono)
    {
        _chunk[_chunkFill * 2] = mono;
        _chunk[_chunkFill * 2 + 1] = mono;

        if (++_chunkFill < ChunkFrames) return;

        FlushChunk();
    }

    /// <summary>
    /// Отдаёт недобранную порцию как есть. Добивать её тишиной до целой нельзя:
    /// хвост короткий (обычно пара кадров), а лишняя тишина в конце каждой фразы
    /// читалась бы как «модуль проглатывает последнее слово».
    /// </summary>
    public void Complete() => FlushChunk();

    private void FlushChunk()
    {
        if (_chunkFill == 0) return;

        var ready = new float[_chunkFill * 2];
        Array.Copy(_chunk, ready, ready.Length);
        _chunkFill = 0;
        _sink(ready);
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
