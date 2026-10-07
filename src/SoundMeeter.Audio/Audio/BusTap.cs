using NAudio.Wave;
using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Ответвление одного входа в одну шину: читает из кольцевого буфера источника
/// и подаёт в микшер шины. Применяет громкость стрипа, индивидуальную посылку
/// на эту шину (GainDb), мут и правило Solo.
///
/// Индивидуальная посылка — это отдельный линейный коэффициент НА ПАРУ (вход, шина),
/// а не громкость шины: в VoiceMeeter так же можно «подмешать» один стрип в одну шину
/// тише другого. Раньше поле GainDb сохранялось в settings.json, но не участвовало
/// в обработке — конфигурация молча работала не так, как показывала.
///
/// Коэффициент читается из живого объекта <see cref="BusRouting"/> на каждом пакете,
/// поэтому изменение в UI слышно сразу, без пересоздания тапа и без разрывов аудио.
///
/// ВАЖНО: my input должен ПЕРЕЗАПИСЫВАТЬ буфер образца для MixingSampleProvider
/// (микшер сам суммирует все входы в общий буфер). Накопление через `+=` здесь
/// запрещено: микшер переиспользует один общий sourceBuffer без очистки, поэтому
/// `+=` прибавляет каждый кадр к остатку предыдущего и сигнал уходит в клипп.
/// ВАЖНО: всегда возвращает запрошенное число сэмплов (тишина при отсутствии
/// данных) — иначе MixingSampleProvider автоматически удалит вход.
/// </summary>
public sealed class BusTap : ISampleProvider
{
    /// <summary>Диапазон индивидуальной посылки, дБ. Симметричный запас сверху
    /// ограничен: прибавление больше +20 дБ к уже усиленному стрипу — это
    /// гарантированный клип на следующей шине.</summary>
    public const float MinGainDb = -60f;
    public const float MaxGainDb = 20f;

    private readonly InputChannelModel _input;
    private readonly BusRouting _routing;
    private readonly SoloState _solo;
    private readonly RingCursor _cursor;
    private readonly float[] _scratch;

    /// <summary>
    /// Звук, подмешанный в стрип снаружи (модуль синтеза речи), либо null.
    ///
    /// Складывается здесь, а не пишется в кольцо источника, и это единственно
    /// возможное место. Кольцо стрипа с устройством уже занято: loopback-захват
    /// кладёт в него ровно столько, сколько шина забирает, — свободной полосы
    /// нет. Добавление туда второй полосы переполняло бы кольцо мгновенно, и
    /// читатель перескакивал бы к последней секунде сообщения.
    ///
    /// Полосу здесь задаёт выходное устройство, поэтому внешний звук забирается
    /// ровно в том темпе, в котором уходит в шину, и отдельный поток-задающий-
    /// темп не нужен вовсе.
    /// </summary>
    private readonly SampleQueue.Reader? _extra;

    private float[]? _extraBuffer;

    public BusTap(RingCursor cursor, InputChannelModel input, BusRouting routing, SoloState solo,
        int readBlockSize = 4096, SampleQueue? extra = null)
    {
        _cursor = cursor;
        _input = input;
        _routing = routing;
        _solo = solo;

        // Свой читатель на каждый тап: иначе две шины разделили бы фразу между собой
        // (см. SampleQueue). Ни одного читателя не остаётся — речь копить незачем.
        _extra = extra?.OpenReader();
        _scratch = new float[Math.Max(readBlockSize, 512)];
    }

    public WaveFormat WaveFormat => InputSource.OutputFormat;

    public int Read(Span<float> buffer)
    {
        int requested = buffer.Length;

        // Читаем из кольца блоками не длиннее scratch: так длина чтения не влияет
        // на память. Раньше scratch разрастался под первый же запрос, а его длина
        // задаётся шириной буфера устройства — на некоторых драйверах это десятки
        // тысяч кадров, и выделение уходило в LOH прямо на потоке рендера.
        // Разбиение безопасно и с точки зрения кадров: scratch всегда кратен
        // размеру кадра (4096), поэтому блоки не режут стереопару пополам.
        if (requested <= _scratch.Length)
        {
            ReadInto(buffer, requested);
            return requested;
        }

        for (int offset = 0; offset < requested; offset += _scratch.Length)
        {
            int chunk = Math.Min(_scratch.Length, requested - offset);
            ReadInto(buffer.Slice(offset, chunk), chunk);
        }
        return requested;
    }

/// <summary>
    /// Один блок чтения: кольцо + внешний звук → gain → <paramref name="target"/>.
    /// </summary>
    private void ReadInto(Span<float> target, int count)
    {
        // Читаем из кольца сколько есть; остальное — тишина.
        int fromRing = _cursor.Read(_scratch, count);
        if (fromRing < count)
            Array.Clear(_scratch, fromRing, count - fromRing);

        // Внешний звук складываем с кольцом в target: держать ради этого ещё один
        // буфер на каждый тап дороже лишнего сложения.
        int fromExtra = 0;
        if (_extra is not null)
        {
            if (_extraBuffer is null || _extraBuffer.Length < count) _extraBuffer = new float[count];
            fromExtra = _extra.Read(_extraBuffer, count);
            if (fromExtra < count) Array.Clear(_extraBuffer, fromExtra, count - fromExtra);
        }

        var ring = _scratch.AsSpan(0, count);

        bool blockedBySolo = _solo.AnyInputSolo && !_input.IsSolo;
        bool muted = _input.IsMuted || blockedBySolo;
        float volume = muted ? 0f : DbToLinear(_input.VolumeDb);
        float send = DbToLinear(_routing.GainDb);

        // Произведение коэффициентов может дать NaN/-Infinity, если в настройках
        // осталось нечисловое значение (правка файла руками, миграция). Sanitize
        // держит в буфере только конечные числа: иначе NaN расходится по всей
        // шине и глушит её целиком, а «пик за пределы float» в выходных данных
        // ловится только на следующем устройстве.
        float gain = volume * send;
        if (!float.IsFinite(gain) || gain == 0f) gain = 0f;
        else if (gain > 4f) gain = 4f; // +12 дБ — предел разумного для одной посылки

        // ВАЖНО: my input должен ПЕРЕЗАПИСЫВАТЬ буфер образца для MixingSampleProvider
        // (микшер сам суммирует все входы в общий буфер). Накопление через `+=` по
        // самому буферу запрещено — микшер переиспользует один общий sourceBuffer без
        // очистки, поэтому `+=` прибавил бы каждый кадр к остатку предыдущего и сигнал
        // ушёл бы в клипп. Сложение ниже — это сложение ДВУХ разных источников
        // (кольцо стрипа и внешний звук), а не накопление по блокам.
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = ring[i];
            if (fromExtra > i) s += _extraBuffer![i];

            s *= gain;
            if (!float.IsFinite(s)) s = 0f;

            target[i] = s;
            float abs = MathF.Abs(s);
            if (abs > peak) peak = abs;
        }

        // VU-уровень входа (обновляется аудио-потоком, читается UI)
        _input.PeakLevel = peak;
    }

    /// <summary>
    /// Тап снят с шины — его читатель больше не нужен. Пока тап жив, читатель держит
    /// очередь от разрастания, а после удаления его позиция перестаёт удерживать
    /// данные, и они освобождаются (см. <see cref="SampleQueue"/>).
    /// </summary>
    public void ReleaseExternalReader() => _extra?.Owner.CloseReader(_extra);

    /// <summary>дБ -> линейный коэффициент с защитой от нечисловых значений.</summary>
    internal static float DbToLinear(float db)
    {
        if (!float.IsFinite(db)) return 1f;
        return MathF.Pow(10f, Math.Clamp(db, -120f, 60f) / 20f);
    }
}
