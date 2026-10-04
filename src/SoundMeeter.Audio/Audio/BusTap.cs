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

    public BusTap(RingCursor cursor, InputChannelModel input, BusRouting routing, SoloState solo,
        int readBlockSize = 4096)
    {
        _cursor = cursor;
        _input = input;
        _routing = routing;
        _solo = solo;
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
    /// Один блок чтения: кольцо → scratch → gain → <paramref name="target"/>.
    /// </summary>
    private void ReadInto(Span<float> target, int count)
    {
        // Читаем из кольца столько, сколько есть; остальное — тишина.
        int read = _cursor.Read(_scratch, count);
        if (read < count)
            Array.Clear(_scratch, read, count - read);

        var source = _scratch.AsSpan(0, count);

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

        // Всегда перезаписываем буфер целиком — микшер суммирует входы сам.
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = source[i] * gain;
            if (!float.IsFinite(s)) s = 0f;
            target[i] = s;
            float abs = MathF.Abs(s);
            if (abs > peak) peak = abs;
        }

        // VU-уровень входа (обновляется аудио-потоком, читается UI)
        _input.PeakLevel = peak;
    }

    /// <summary>дБ -> линейный коэффициент с защитой от нечисловых значений.</summary>
    internal static float DbToLinear(float db)
    {
        if (!float.IsFinite(db)) return 1f;
        return MathF.Pow(10f, Math.Clamp(db, -120f, 60f) / 20f);
    }
}
