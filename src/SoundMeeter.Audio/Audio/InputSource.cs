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

    /// <summary>
    /// Эффекты стрипа (SM-B05): компрессор, trim, задержка, реверберация.
    /// Живут между пакетами, поэтому создаются один раз на источник: пересоздание
    /// обнуляло бы огибающую компрессора и «съедало» хвост задержки.
    /// </summary>
    private readonly StripDsp _effects;

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

    /// <summary>
    /// Формат и размер кадра, прочитанные один раз при старте. Раньше обработчик
    /// пакета брал их у <c>IWaveIn.WaveFormat</c> на КАЖДЫЙ пакет — лишний
    /// вызов через виртуальное свойство в самом горячем потоке.
    /// </summary>
    private WaveFormat? _waveFormat;
    private int _bytesPerFrame;

    /// <summary>Максимальный размер пакета за работу источника (диагностика).</summary>
    private int _maxPacketBytes;

    /// <summary>Предупреждение о просрочке уже выдано — не повторяем каждый пакет.</summary>
    private bool _warnedBacklog;

    public InputSource(InputChannelModel model)
    {
        _model = model;
        _effects = new StripDsp(model);
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

        // MMDevice — COM-обёртка над IMMDevice, финализатора у неё нет (SM-A08):
        // без Dispose ссылка на устройство не отпускается никогда, а Start()
        // зовётся при каждой пересборке роутинга. WasapiCapture и
        // WasapiLoopbackCapture устройство не удерживают (в WasapiCapture
        // остаётся только IAudioClient), поэтому освобождать его здесь безопасно.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(_model.DeviceId);
        if (device == null) throw new InvalidOperationException($"Device not found: {_model.DeviceId}");

        // Описание формата читаем здесь, а не в обработчике пакета:
        // WasapiCapture.WaveFormat каждый раз проходит через
        // AsStandardWaveFormat(), и на пакетах лишних обращений быть не должно.
        WaveFormat actualFormat;

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
            actualFormat = capture.WaveFormat;
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
            actualFormat = loopback.WaveFormat;
        }

        _waveFormat = actualFormat;
        _bytesPerFrame = Math.Max(1, actualFormat.Channels * actualFormat.BitsPerSample / 8);

        _logger.LogInformation(
            "Strip «{Strip}» opened: device={Device} mic={IsMic} requested={Rate} Hz/{Channels} ch, actual={Actual}",
            _model.Name, _model.DeviceId, _model.IsMicrophone, SampleRate, Channels,
            Describe(actualFormat));
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
        _waveFormat = null;
        _bytesPerFrame = 0;
        _maxPacketBytes = 0;
        _warnedBacklog = false;

        // Старт эффектов «с нуля»: продолжение прерванного хвоста задержки или
        // реверберации дало бы щелчок в первом же пакете после перезапуска.
        _effects.Reset();

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

        // Формат читаем из кэша, а не у отправителя: WasapiCapture.WaveFormat
        // каждый раз конструирует новый объект через AsStandardWaveFormat(),
        // а это ~100 вызовов в секунду на источник лишней работы в аудиопотоке.
        var fmt = _waveFormat;
        if (fmt == null) return;

        TrackPacketSize(e.BytesRecorded);

        try
        {
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

            // 3.5) Входное усиление канала. Стоит именно здесь, до денойзера и до
            //      разветвления на шины: один раз на пакет, а не в каждой посылке,
            //      и RNNoise получает сигнал рабочего уровня, а не тихий.
            ApplyGain(outBuf, outFrames);

            // 4) Денойзер RNNoise (48кГц/стерео) — только если включён и доступен.
            //    Процессор держит фиксированную задержку в 10 мс и всегда
            //    возвращает ровно outFrames, поэтому буфер не раздувается.
            if (_model.DenoiserEnabled && _denoiser != null)
                _denoiser.Process(outBuf, outFrames);

            // 5) Эффекты стрипа (SM-B05): компрессор → trim → задержка →
            //    реверберация. Стоят здесь, до записи в кольцо: блоки с
            //    состоянием считаются один раз на пакет иначе, а во всех
            //    посылках стрипа звучали бы по-разному.
            _effects.Process(outBuf, outFrames);

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
    /// Применяет входное усиление канала к готовому пакету. Пакет приходит
    /// свободным от предыдущей обработки (ресемплер пишет в отдельный буфер), но
    /// путь «без ресемплинга» отдаёт <see cref="_stereo"/> — тот же массив переиспользуется
    /// следующим пакетом, поэтому лишнее умножение там безвредно.
    /// </summary>
    private void ApplyGain(float[] buffer, int frames)
    {
        float gain = DbToLinear(_model.GainDb);
        if (gain == 1f) return;

        int samples = frames * Channels;
        if (samples > buffer.Length) samples = buffer.Length;

        for (int i = 0; i < samples; i++)
        {
            float v = buffer[i] * gain;
            // +60 дБ с тихого микрофона вполне может уйти в бесконечность при
            // NaN в отсчётах: в кольцо такое писать нельзя, NaN распространяется
            // по всей посылке и глушит канал целиком.
            buffer[i] = float.IsFinite(v) ? Math.Clamp(v, -4f, 4f) : 0f;
        }
    }

    private static float DbToLinear(float db) =>
        !float.IsFinite(db) || db <= -60f ? 0f : (float)Math.Pow(10.0, db / 20.0);

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

    /// <summary>
    /// Следит за размером пакета и предупреждает о коплении.
    ///
    /// NAudio 3.x выделяет <c>new byte[bytesAvailable]</c> на каждый пакет и
    /// вычитывает буфер WASAPI целиком, пока тот не опустеет. Пока поток успевает,
    /// пакет — это 10 мс (несколько килобайт, gen0, сборщик его убирает). Но если
    /// обработка хоть раз отстала, пакет растёт; за 85 КБ он уезжает в LOH, а LOH
    /// собирается только в gen2 и не уплотняется — рабочая память растёт часами
    /// без видимой причины.
    ///
    /// Поэтому размер и «просрочка» пишутся в журнал: по этим числам видно, был
    /// ли захват реально перегружен, а не просто много аллоцирует gen0.
    /// </summary>
    private void TrackPacketSize(int bytes)
    {
        if (bytes > _maxPacketBytes)
        {
            _maxPacketBytes = bytes;
            _logger.LogWarning(
                "Strip «{Strip}»: пакет {Bytes} байт ({Ms:F1} мс) — заметно больше 10 мс, " +
                "поток захвата отстаёт; такие пакеты уходят в LOH",
                _model.Name, bytes, bytes * 1000.0 / Math.Max(1, _bytesPerFrame * SampleRate));
        }

        // Просрочка: сколько данных скопилось в буфере WASAPI сверх положенного.
        // WASAPI отдаёт это в GetCurrentPadding, но он же требует лишнего вызова
        // в аудиопотоке, поэтому грубая оценка по размеру пакета достаточна.
        int normal = _bytesPerFrame * 480;   // 10 мс при 48 кГц
        if (normal > 0 && bytes > normal * 4 && !_warnedBacklog)
        {
            _warnedBacklog = true;
            _logger.LogWarning(
                "Strip «{Strip}»: пакет вырос до {Bytes} байт (норма {Normal}) — " +
                "буфер WASAPI не успевает опустошаться, звук уже отстаёт",
                _model.Name, bytes, normal);
        }
        else if (bytes <= normal * 2)
        {
            _warnedBacklog = false;
        }
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
