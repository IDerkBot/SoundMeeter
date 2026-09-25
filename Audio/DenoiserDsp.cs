using RNNoise.NET;
using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Денойзер на движке RNNoise (CPU, открытый) для входного канала.
/// Обрабатывает канал L/R по отдельности кадрами по 480 сэмплов (10 мс @ 48кГц).
/// Второй порядок каскада: RNNoise -> formant-EQ (Low/Mid/High пики + Group makeup-gain) -> dry/wet.
/// Поскольку RNNoise не имеет параметров интенсивности, параметр "Noise Remover"
/// задаёт долю денойзерного сигнала в миксе с исходным.
/// </summary>
public sealed class DenoiserDsp : IDisposable
{
    private const int FrameSize = 480;
    private const float Q = 1.0f;
    private const float LowFreq = 500f;
    private const float MidFreq = 1500f;
    private const float HighFreq = 3500f;

    private readonly InputChannelModel _model;
    private readonly Denoiser _denoiseL = new();
    private readonly Denoiser _denoiseR = new();

    private readonly float[] _pendingL = new float[1 << 16];
    private readonly float[] _pendingR = new float[1 << 16];
    private int _pendingFrames;

    private readonly float[] _dryL = new float[FrameSize];
    private readonly float[] _dryR = new float[FrameSize];
    private readonly float[] _wetL = new float[FrameSize];
    private readonly float[] _wetR = new float[FrameSize];

    private Biquad _eqLowL, _eqMidL, _eqHighL;
    private Biquad _eqLowR, _eqMidR, _eqHighR;

    public DenoiserDsp(InputChannelModel model)
    {
        _model = model;
    }

    /// <summary>
    /// Обрабатывает стерео-буфер 48кГц и возвращает число записанных кадров (<= frames).
    /// Если денойзер выключен параметрами (Noise Remover = 0 или Dry/Wet = 0), сигнал проходит без задержки.
    /// </summary>
    public int Process(float[] stereo, int frames)
    {
        float nr = Math.Clamp(_model.DenoiserNoiseRemover, 0f, 100f) / 100f;
        float dw = Math.Clamp(_model.DenoiserDryWet, 0f, 100f) / 100f;
        if (nr <= 0f || dw <= 0f) return frames;

        int p = _pendingFrames;
        for (int i = 0; i < frames; i++)
        {
            _pendingL[p + i] = stereo[i * 2];
            _pendingR[p + i] = stereo[i * 2 + 1];
        }
        _pendingFrames += frames;

        UpdateEqCoefficients(
            Math.Clamp(_model.DenoiserFormantLowDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantMidDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantHighDb, -24f, 24f),
            Math.Clamp(_model.DenoiserFormantGroupDb, -12f, 12f));

        int outFrames = 0;
        int front = 0;
        while (_pendingFrames - front >= FrameSize)
        {
            ProcessFrame(stereo, front, outFrames, nr, dw);
            front += FrameSize;
            outFrames += FrameSize;
        }

        int rem = _pendingFrames - front;
        if (rem > 0)
        {
            Array.Copy(_pendingL, front, _pendingL, 0, rem);
            Array.Copy(_pendingR, front, _pendingR, 0, rem);
        }
        _pendingFrames = rem;
        return outFrames;
    }

    private void ProcessFrame(float[] stereo, int front, int at, float nr, float dw)
    {
        for (int i = 0; i < FrameSize; i++)
        {
            _dryL[i] = _pendingL[front + i];
            _dryR[i] = _pendingR[front + i];
            _wetL[i] = _dryL[i];
            _wetR[i] = _dryR[i];
        }

        _denoiseL.Denoise(_wetL.AsSpan(0, FrameSize), false);
        _denoiseR.Denoise(_wetR.AsSpan(0, FrameSize), false);

        for (int i = 0; i < FrameSize; i++)
        {
            float el = _eqLowL.Process(_eqMidL.Process(_eqHighL.Process(_wetL[i]))) * GroupGain;
            float er = _eqLowR.Process(_eqMidR.Process(_eqHighR.Process(_wetR[i]))) * GroupGain;

            float l = _dryL[i] + (el - _dryL[i]) * nr;
            float r = _dryR[i] + (er - _dryR[i]) * nr;

            stereo[(at + i) * 2] = _dryL[i] + (l - _dryL[i]) * dw;
            stereo[(at + i) * 2 + 1] = _dryR[i] + (r - _dryR[i]) * dw;
        }
    }

    private void UpdateEqCoefficients(float lowDb, float midDb, float highDb, float groupDb)
    {
        _eqLowL.SetPeaking(LowFreq, lowDb, Q);
        _eqMidL.SetPeaking(MidFreq, midDb, Q);
        _eqHighL.SetPeaking(HighFreq, highDb, Q);
        _eqLowR.SetPeaking(LowFreq, lowDb, Q);
        _eqMidR.SetPeaking(MidFreq, midDb, Q);
        _eqHighR.SetPeaking(HighFreq, highDb, Q);
        GroupGain = MathF.Pow(10f, groupDb / 20f);
    }

    private float GroupGain { get; set; } = 1f;

    public void Dispose()
    {
        _denoiseL.Dispose();
        _denoiseR.Dispose();
    }

    /// <summary>Биквад-пик (RBJ cookbook), Direct Form I.</summary>
    private struct Biquad
    {
        public float B0, B1, B2, A1, A2;
        public float X1, X2, Y1, Y2;

        public void SetPeaking(float freq, float gainDb, float q)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freq / InputSource.SampleRate;
            float alpha = MathF.Sin(w0) / (2f * q);
            float cos = MathF.Cos(w0);

            float b0 = 1f + alpha * a;
            float b1 = -2f * cos;
            float b2 = 1f - alpha * a;
            float a0 = 1f + alpha / a;
            float a1 = -2f * cos;
            float a2 = 1f - alpha / a;

            B0 = b0 / a0;
            B1 = b1 / a0;
            B2 = b2 / a0;
            A1 = a1 / a0;
            A2 = a2 / a0;
        }

        public float Process(float x)
        {
            float y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
            X2 = X1;
            X1 = x;
            Y2 = Y1;
            Y1 = y;
            return y;
        }
    }
}