using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;

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
    private readonly ILogger _logger = AppLog.For<InputSource>();
    private IWaveIn? _waveIn;

    /// <summary>
    /// Ресемплер на случай, если устройство отдаёт не 48 кГц. Пересоздаётся при
    /// смене частоты (переподключение устройства, смена формата по требованию
    /// драйвера) — состояние фильтра при этом не наследуется намеренно: старт
    /// «с нуля» на пустом кольцевом буфере даёт плавный выход из тишины, тогда
    /// как продолжение прерванного состояния дало бы щелчок.
    /// </summary>
    private PolyphaseResampler? _resampler;
    private int _resamplerRate;

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
            _logger.LogWarning(ex, "RNNoise недоступен для «{Strip}»: {Message}", _model.Name, ex.Message);
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

        _logger.LogInformation(
            "Strip «{Strip}» opened: device={Device} mic={IsMic} requested={Rate} Hz/{Channels} ch, actual={Actual}",
            _model.Name, _model.DeviceId, _model.IsMicrophone, SampleRate, Channels,
            Describe(_waveIn.WaveFormat));
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
            _logger.LogWarning(ex, "Остановка захвата «{Strip}» не удалась: {Message}", _model.Name, ex.Message);
        }
        waveIn.Dispose();

        _resampler = null;
        _resamplerRate = 0;
        _logger.LogInformation("Strip «{Strip}» closed: device={Device}", _model.Name, _model.DeviceId);
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

            // 3) Частота -> 48кГц. WASAPI в общем режиме сам приводит поток к
            //    запрошенному формату, но драйвер вправе отдать и свой: тогда
            //    пересчёт делает ресемплер с полосой ограничения, а не линейная
            //    интерполяция (SM-A01).
            float[] outBuf = _stereo;
            int outFrames = frames;
            if (fmt.SampleRate != SampleRate)
            {
                outFrames = Resample(_stereo, frames, fmt.SampleRate);
                outBuf = _resampled;
            }
            else
            {
                _resampler = null;
                _resamplerRate = 0;
            }

            if (outFrames <= 0) return;

            // 4) Денойзер RNNoise (48кГц/стерео) — только если включён и доступен.
            //    Процессор держит фиксированную задержку в 10 мс и всегда
            //    возвращает ровно outFrames, поэтому буфер не раздувается.
            if (_model.DenoiserEnabled && _denoiser != null)
                _denoiser.Process(outBuf, outFrames);

            _ring.Write(outBuf.AsSpan(0, outFrames * 2));
        }
        catch (Exception ex)
        {
            // Исключение здесь проглатывает целый пакет аудио, поэтому след
            // обязателен: иначе поломка видна только как «звук иногда пропадает».
            _logger.LogError(ex, "Ошибка обработки пакета на «{Strip}»: {Message}", _model.Name, ex.Message);
        }
    }

    /// <summary>
    /// Приводит пакет к 48 кГц и возвращает число кадров в <see cref="_resampled"/>.
    /// Длина считается ресемплером накопительно (точное рациональное отношение),
    /// поэтому «плавающего» буфера не возникает; здесь только выделяется память
    /// с запасом на округление.
    /// </summary>
    private int Resample(float[] source, int frames, int sourceRate)
    {
        if (_resampler == null || _resamplerRate != sourceRate)
        {
            _resampler = new PolyphaseResampler(sourceRate, SampleRate, Channels);
            _resamplerRate = sourceRate;
            _logger.LogInformation(
                "Strip «{Strip}»: ресемплер {From} -> {To} Hz, {Taps} отсчётов фильтра",
                _model.Name, sourceRate, SampleRate, _resampler.Taps);
        }

        var capacity = (int)Math.Ceiling(frames * (double)SampleRate / sourceRate) + 8;
        if (_resampled.Length < capacity * Channels)
            _resampled = new float[capacity * Channels];

        return _resampler.Process(source, frames, _resampled, capacity);
    }

    private static string Describe(WaveFormat format) =>
        $"{format.SampleRate} Hz/{format.Channels} ch/{format.BitsPerSample} bit {format.Encoding}";

    /// <summary>
    /// Приводит межленточные байты к float. Поддерживаются IEEE float32 и PCM
    /// 16/24/32 бит — все четыре формата обрабатываются одинаково и одинаково
    /// ограничены длиной пакета: лишние «хвостовые» байты (их не бывает у WASAPI,
    /// но бывает у программных источников) игнорируются, а не портят соседние сэмплы.
    /// </summary>
    private static void DecodeToFloat(byte[] src, int byteCount, WaveFormat fmt, float[] dst, int frames)
    {
        int channels = fmt.Channels;
        int total = frames * channels;
        if (total > dst.Length) total = dst.Length;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int samples = Math.Min(total, byteCount / 4);
            if (samples > 0) Buffer.BlockCopy(src, 0, dst, 0, samples * 4);
            if (samples < total) Array.Clear(dst, samples, total - samples);
            return;
        }

        switch (fmt.BitsPerSample)
        {
            case 16:
            {
                int samples = Math.Min(total, byteCount / 2);
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                if (samples < total) Array.Clear(dst, samples, total - samples);
                break;
            }
            case 24:
            {
                int framesToRead = Math.Min(frames, byteCount / (channels * 3));
                int written = 0;
                for (int f = 0; f < framesToRead; f++)
                {
                    for (int c = 0; c < channels; c++, written++)
                    {
                        int b = f * channels * 3 + c * 3;
                        int value = src[b] | (src[b + 1] << 8) | (src[b + 2] << 16);
                        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                        dst[written] = value / 8388608f;
                    }
                }
                if (written < total) Array.Clear(dst, written, total - written);
                break;
            }
            case 32:
            {
                int samples = Math.Min(total, byteCount / 4);
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                if (samples < total) Array.Clear(dst, samples, total - samples);
                break;
            }
            default:
                Array.Clear(dst, 0, total);
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
}
