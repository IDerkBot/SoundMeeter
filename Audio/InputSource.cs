using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundMeeter.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundMeeter.Audio;

/// <summary>
/// Источник аудио для входного канала. Захватывает устройство
/// (микрофон или loopback устройства воспроизведения) и кладёт
/// float-сэмплы 48кГц/стерео в общий кольцевой буфер, откуда
/// его читают все шины (через RingCursor).
/// </summary>
public sealed class InputSource
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BufferSeconds = 1;
    public static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private readonly InputChannelModel _model;
    private readonly SampleRingBuffer _ring = new(SampleRate * Channels * BufferSeconds);
    private readonly DenoiserDsp? _denoiser;
    private IWaveIn? _waveIn;

    // Буферы конверсии
    private float[] _decoded = Array.Empty<float>();
    private float[] _stereo = Array.Empty<float>();
    private float[] _resampled = Array.Empty<float>();

    public InputSource(InputChannelModel model)
    {
        _model = model;
        try
        {
            _denoiser = new DenoiserDsp(model);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RNNoise unavailable: {ex.Message}");
            _denoiser = null;
        }
    }

    public bool IsRunning => _waveIn != null;

    public RingCursor OpenCursor() => _ring.OpenCursor();

    /// <summary>
    /// Стартует захват устройства.
    /// </summary>
    public void Start()
    {
        if (_waveIn != null) return;

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(_model.DeviceId);
        if (device == null) throw new InvalidOperationException($"Device not found: {_model.DeviceId}");

        if (_model.IsMicrophone)
        {
            var capture = new WasapiCapture(device)
            {
                WaveFormat = OutputFormat,
                ShareMode = AudioClientShareMode.Shared
            };
            capture.DataAvailable += OnDataAvailable;
            capture.StartRecording();
            _waveIn = capture;
        }
        else
        {
            var loopback = new WasapiLoopbackCapture(device)
            {
                WaveFormat = OutputFormat
            };
            loopback.DataAvailable += OnDataAvailable;
            loopback.StartRecording();
            _waveIn = loopback;
        }
    }

    public void Stop()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn == null) return;

        try
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stop capture failed for {_model.Name}: {ex.Message}");
        }
        waveIn.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _denoiser?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        if (sender is not IWaveIn source) return;

        try
        {
            var fmt = source.WaveFormat;
            int bytesPerSample = Math.Max(1, fmt.BitsPerSample / 8);
            int frames = e.BytesRecorded / (fmt.Channels * bytesPerSample);
            if (frames == 0) return;

            // 1) Байты -> float (межленточные сэмплы в исходном формате)
            if (_decoded.Length < frames * fmt.Channels)
                _decoded = new float[frames * fmt.Channels];
            DecodeToFloat(e.Buffer, e.BytesRecorded, fmt, _decoded, frames);

            // 2) Каналы -> стерео (2 канала)
            if (_stereo.Length < frames * 2)
                _stereo = new float[frames * 2];
            ToStereo(_decoded, frames, fmt.Channels, _stereo);

            // Mono стрипа: сводим L+R в центр (как кнопка Mono в VoiceMeeter)
            if (_model.IsMono)
            {
                for (int f = 0; f < frames; f++)
                {
                    float avg = (_stereo[f * 2] + _stereo[f * 2 + 1]) * 0.5f;
                    _stereo[f * 2] = avg;
                    _stereo[f * 2 + 1] = avg;
                }
            }

            // 3) Частота -> 48кГц
            float[] outBuf = _stereo;
            int outFrames = frames;
            if (fmt.SampleRate != SampleRate)
            {
                outFrames = (int)((long)frames * SampleRate / fmt.SampleRate);
                if (_resampled.Length < outFrames * 2)
                    _resampled = new float[outFrames * 2];
                ResampleLinear(_stereo, frames, fmt.SampleRate, _resampled, outFrames);
                outBuf = _resampled;
            }

            // 4) Денойзер RNNoise (48кГц/стерео) — только если включён и доступен
            if (_model.DenoiserEnabled && _denoiser != null)
            {
                outFrames = _denoiser.Process(outBuf, outFrames);
                if (outFrames == 0) return;
            }

            _ring.Write(outBuf.AsSpan(0, outFrames * 2));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Input {_model.Name} capture error: {ex.Message}");
        }
    }

    private static void DecodeToFloat(byte[] src, int byteCount, WaveFormat fmt, float[] dst, int frames)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            Buffer.BlockCopy(src, 0, dst, 0, byteCount);
            return;
        }

        int channels = fmt.Channels;
        switch (fmt.BitsPerSample)
        {
            case 16:
                for (int i = 0, p = 0; i < byteCount / 2; i++, p++)
                    dst[p] = BitConverter.ToInt16(src, i * 2) / 32768f;
                break;
            case 24:
                for (int i = 0, p = 0; i < frames; i++)
                {
                    for (int c = 0; c < channels; c++, p++)
                    {
                        int b = i * channels * 3 + c * 3;
                        int value = src[b] | (src[b + 1] << 8) | (src[b + 2] << 16);
                        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                        dst[p] = value / 8388608f;
                    }
                }
                break;
            case 32:
                for (int i = 0, p = 0; i < byteCount / 4; i++, p++)
                    dst[p] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                break;
            default:
                Array.Clear(dst, 0, frames * channels);
                break;
        }
    }

    private void ToStereo(float[] src, int frames, int srcChannels, float[] dst)
    {
        for (int f = 0; f < frames; f++)
        {
            float left, right;
            if (srcChannels >= 2)
            {
                left = src[f * srcChannels];
                right = src[f * srcChannels + 1];
            }
            else
            {
                left = right = src[f];
            }
            dst[f * 2] = left;
            dst[f * 2 + 1] = right;
        }
    }

    private static void ResampleLinear(float[] src, int srcFrames, int srcRate, float[] dst, int dstFrames)
    {
        if (srcFrames <= 1) return;
        double ratio = srcRate / (double)SampleRate; // позиция входа на один выходной сэмпл

        for (int i = 0; i < dstFrames; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, srcFrames - 1);
            float frac = (float)(pos - i0);

            dst[i * 2] = src[i0 * 2] + (src[i1 * 2] - src[i0 * 2]) * frac;
            dst[i * 2 + 1] = src[i0 * 2 + 1] + (src[i1 * 2 + 1] - src[i0 * 2 + 1]) * frac;
        }
    }
}