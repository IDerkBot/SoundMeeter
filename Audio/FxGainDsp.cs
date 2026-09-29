using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Уровень стрипа после компрессора: отдельный, двусторонний Trim. В отличие от
/// фейдера канала он стоит ДО задержки и реверберации, поэтому им выравнивают
/// громкость эффектов, не трогая посылки стрипа на шины; в отличие от входного
/// Gain микрофона (0..+60 дБ, только подъём тихого сигнала) умеет и убавлять.
///
/// Само значение сглаживается: прыжок усиления на пакете слышен щелчком, а
/// крутилка даёт его постоянно. Выключенный блок — единичный коэффициент,
/// состояние при этом продолжает сглаживаться, чтобы включение не щёлкнуло.
/// </summary>
public sealed class FxGainDsp
{
    private const float SmoothMs = 15f;

    private readonly InputChannelModel _model;
    private float _current = 1f;

    public FxGainDsp(InputChannelModel model) => _model = model;

    public void Process(float[] stereo, int frames)
    {
        int samples = frames * 2;
        if (samples > stereo.Length) samples = stereo.Length & ~1;
        if (samples <= 0) return;

        float target = _model.FxGainEnabled
            ? CompressorDsp.DbToLinear(CompressorDsp.Clamp(_model.FxGainDb, -60f, 24f))
            : 1f;
        float coef = 1f - 1f * MathF.Exp(-1000f / (SmoothMs * InputSource.SampleRate));

        for (int i = 0; i < samples; i++)
        {
            _current += (target - _current) * coef;
            stereo[i] *= _current;
        }
    }

    public void Reset() => _current = 1f;
}
