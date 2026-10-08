using SoundMeeter.Audio;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Прогон сигнала через <see cref="StripDsp"/> так же, как это делает
/// <c>InputSource</c>: пакетами по 480 кадров (один пакет WASAPI при 48 кГц),
/// каждый со смещения 0. Если звать <c>Process</c> на одном и том же массиве,
/// повторно обработается первый кусок, а хвост останется нетронутым — тест
/// будет врать о том, что DSP делает с сигналом.
/// </summary>
public static class Signal
{
    public const int SampleRate = 48000;

    /// <summary>Кадров в одном пакете — как у WASAPI при 48 кГц.</summary>
    public const int ChunkFrames = 480;

    public static float[] Run(StripDsp dsp, float[] input)
    {
        var result = new float[input.Length];
        var packet = new float[ChunkFrames * 2];
        int frames = input.Length / 2;

        for (int f = 0; f < frames; f += ChunkFrames)
        {
            int count = Math.Min(ChunkFrames, frames - f);
            Array.Copy(input, f * 2, packet, 0, count * 2);
            dsp.Process(packet, count);
            Array.Copy(packet, 0, result, f * 2, count * 2);
        }

        return result;
    }

    /// <summary>Одиночный импульс: удобно проверять задержку и реверберацию.</summary>
    public static float[] Impulse(int samples, int offset = 0, float amplitude = 1f)
    {
        var buffer = new float[samples * 2];
        buffer[offset * 2] = amplitude;
        return buffer;
    }

    /// <summary>Синус в обеих каналах.</summary>
    public static float[] Sine(int samples, float amplitude, float hz = 440f)
    {
        var buffer = new float[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            float v = amplitude * MathF.Sin(2f * MathF.PI * hz * i / SampleRate);
            buffer[i * 2] = v;
            buffer[i * 2 + 1] = v;
        }

        return buffer;
    }

    public static float Peak(float[] buffer)
    {
        float peak = 0f;
        foreach (float v in buffer) peak = MathF.Max(peak, MathF.Abs(v));
        return peak;
    }

    /// <summary>Энергия сигнала начиная с указанного кадра.</summary>
    public static float EnergyFrom(float[] buffer, int fromFrame)
    {
        float sum = 0f;
        for (int i = fromFrame * 2; i < buffer.Length; i++) sum += buffer[i] * buffer[i];
        return sum;
    }
}
