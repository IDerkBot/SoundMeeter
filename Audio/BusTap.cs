using NAudio.Wave;
using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Ответвление одного входа в одну шину: читает из кольцевого буфера источника
/// и подаёт в микшер шины. Применяет громкость стрипа, мут и правило Solo.
/// ВАЖНО: my input должен ПЕРЕЗАПИСЫВАТЬ буфер образца для MixingSampleProvider
/// (микшер сам суммирует все входы в общий буфер). Накопление через `+=` здесь
/// запрещено: микшер переиспользует один общий sourceBuffer без очистки, поэтому
/// `+=` прибавляет каждый кадр к остатку предыдущего и сигнал уходит в клипп.
/// ВАЖНО: всегда возвращает запрошенное число сэмплов (тишина при отсутствии
/// данных) — иначе MixingSampleProvider автоматически удалит вход.
/// </summary>
public sealed class BusTap : ISampleProvider
{
    private readonly InputChannelModel _input;
    private readonly SoloState _solo;
    private readonly RingCursor _cursor;
    private float[] _scratch;

    public BusTap(RingCursor cursor, InputChannelModel input, SoloState solo, int readBlockSize = 4096)
    {
        _cursor = cursor;
        _input = input;
        _solo = solo;
        _scratch = new float[Math.Max(readBlockSize, 512)];
    }

    public WaveFormat WaveFormat => InputSource.OutputFormat;

    public int Read(Span<float> buffer)
    {
        int requested = buffer.Length;

        if (_scratch.Length < requested)
            _scratch = new float[requested];

        // Читаем из кольца столько, сколько есть; остальное — тишина.
        int read = _cursor.Read(_scratch, requested);
        if (read < requested)
            Array.Clear(_scratch, read, requested - read);

        bool blockedBySolo = _solo.AnyInputSolo && !_input.IsSolo;
        bool muted = _input.IsMuted || blockedBySolo;
        float volume = muted ? 0f : DbToLinear(_input.VolumeDb);

        // Всегда перезаписываем буфер целиком — микшер суммирует входы сам.
        float peak = 0f;
        for (int i = 0; i < requested; i++)
        {
            float s = _scratch[i] * volume;
            buffer[i] = s;
            float abs = MathF.Abs(s);
            if (abs > peak) peak = abs;
        }

        // VU-уровень входа (обновляется аудио-потоком, читается UI)
        _input.PeakLevel = peak;
        return requested;
    }

    internal static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}