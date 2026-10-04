using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
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
    private const int BufferSeconds = AudioEngineDefaults.RingBufferMilliseconds / 1000;
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
    private WasapiRecorder? _recorder;

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

    /// <summary>
    /// Сколько кадров денойзера считается нормой: один кадр RNNoise на старте
    /// (10 мс) плюс небольшой запас на «неровный» первый пакет.
    /// </summary>
    private const long GlitchReportThreshold = 480;

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

    public bool IsRunning => _recorder != null;

    public RingCursor OpenCursor() => _ring.OpenCursor();

    /// <summary>
    /// Стартует захват устройства.
    ///
    /// Здесь и раньше была причина половины задержки микшера. У
    /// <c>WasapiCapture</c>/<c>WasapiLoopbackCapture</c> в NAudio 3.1 нет
    /// конструктора с параметрами: единственная доступная цепочка ведёт в
    /// <c>(device, useEventSync: false, 100)</c>, то есть <b>100 мс буфера и
    /// опрос потока по таймеру</b> вместо события. У loopback-варианта другого
    /// конструктора не существует вообще — то есть уменьшить задержку входа
    /// через него было физически нечем. <c>WasapiRecorderBuilder</c> даёт и то и
    /// другое, плюс MMCSS-приоритет потока и готовые счётчики латентности.
    /// </summary>
    public void Start()
    {
        if (_recorder != null) return;

        // MMDevice — COM-обёртка над IMMDevice, финализатора у неё нет (SM-A08):
        // без Dispose ссылка на устройство не отпускается никогда, а Start()
        // зовётся при каждой пересборке роутинга. WasapiRecorder устройство не
        // удерживает (внутри остаётся только IAudioClient), поэтому освобождать
        // его здесь безопасно.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(_model.DeviceId);
        if (device == null) throw new InvalidOperationException($"Device not found: {_model.DeviceId}");

        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            // По событию, а не по таймеру: опрос добавлял к задержке до половины
            // периода «дрожания» и ровнял пакеты по пачкам.
            .WithEventSync()
            .WithBufferLength(AudioEngineDefaults.InputBufferMilliseconds)
            // Поток захвата должен обгонять поток рендера, иначе сборщик мусора
            // и любой посторонний поток выбивают у него пакеты — и тогда в звуке
            // не задержка, а разрывы.
            .WithMmcssThreadPriority("Pro Audio")
            .WithFormat(OutputFormat);

        if (!_model.IsMicrophone) builder = builder.WithLoopbackCapture();

        var recorder = builder.Build();

        // Формат известен до старта, поэтому выставляем его ДО StartRecording():
        // обработчик пакета на запускающем потоке не должен увидеть пустое
        // поле и выбросить первый пакет (SM: гонка старта).
        _waveFormat = recorder.WaveFormat;
        _bytesPerFrame = Math.Max(1, _waveFormat.BlockAlign);

        recorder.DataAvailable += OnDataAvailable;
        _recorder = recorder;

        try
        {
            recorder.StartRecording();
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            _recorder = null;
            recorder.Dispose();
            throw;
        }

// LatencyMilliseconds здесь — НЕ измерение: в стандартном пути WasapiRecorder
        // присваивает ему буфер, который запросили мы (WasapiRecorder.cs:331), и
        // перезаписывает только в низколатентном режиме IAudioClient3. Поэтому
        // пишем его как запрос, а настоящую величину берём из CurrentLatency —
        // она считается через GetCurrentPadding, то есть это реальная глубина
        // буфера устройства (см. LatencyDiagnostics в WasapiAudioEngine).
        _logger.LogInformation(
            "Strip «{Strip}» opened: device={Device} mic={IsMic} format={Format}, " +
            "requested latency={Requested} ms",
            _model.Name, _model.DeviceId, _model.IsMicrophone, Describe(_waveFormat),
            AudioEngineDefaults.InputBufferMilliseconds);
    }

    public void Stop()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return;

        try
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Остановка захвата «{Strip}» не удалась: {Message}", _model.Name, ex.Message);
        }
        // Dispose дожидается потока захвата и освобождает IAudioClient.
        recorder.Dispose();

        _resampler = null;
        _resamplerRate = 0;
        _waveFormat = null;
        _bytesPerFrame = 0;
        _maxPacketBytes = 0;
        _warnedBacklog = false;

        // Старт эффектов «с нуля»: продолжение прерванного хвоста задержки или
        // реверберации дало бы щелчок в первом же пакете после перезапуска.
        _effects.Reset();

        LogDenoiserGlitches();

        _logger.LogInformation("Strip «{Strip}» closed: device={Device}", _model.Name, _model.DeviceId);
    }

    /// <summary>
    /// Пишет в журнал, если денойзеру пришлось подмешивать тишину или отбрасывать
    /// вход. Оба числа в норме нулевые (единицы — только первый кадр после
    /// включения), и ненулевое значение означает, что пакет длиннее очереди
    /// денойзера, то есть звук рвётся — ровно тот случай, когда «кажется, что
    /// шумоподавление трещит».
    /// </summary>
    private void LogDenoiserGlitches()
    {
        var denoiser = _denoiser;
        if (denoiser == null) return;
        if (denoiser.UnderrunFrames <= GlitchReportThreshold &&
            denoiser.OverrunFrames <= GlitchReportThreshold)
            return;

        _logger.LogWarning(
            "Strip «{Strip}»: денойзер подмешал {Underrun} и отбросил {Overrun} кадров — " +
            "конвейер не успевает за пакетом, в звуке будут щелчки",
            _model.Name, denoiser.UnderrunFrames, denoiser.OverrunFrames);
    }

    public void Dispose()
    {
        Stop();
        _denoiser?.Dispose();
    }

    /// <summary>
    /// Пакет от <see cref="WasapiRecorder"/>. Буфер — <c>ReadOnlySpan</c> без
    /// копирования, и он действителен только внутри этого вызова, поэтому всё
    /// ниже сразу разбирается в переиспользуемые массивы источника и нигде не
    /// сохраняется. Флаг <c>Silent</c> означает настоящую тишину в буфере, а не
    /// «устройство ничего не отдало»: конвейер всё равно нужно прогнать на
    /// нулях, иначе затухающие хвосты задержки, реверберации и огибающая
    /// компрессора замрут и «застрянут» до следующего непустого пакета.
    /// </summary>
    private void OnDataAvailable(ReadOnlySpan<byte> packet, AudioClientBufferFlags flags,
        long devicePosition, long qpcPosition)
    {
        int bytesRecorded = packet.Length;
        if (bytesRecorded <= 0) return;

        // Формат читаем из кэша, а не у отправителя: обращение к свойству
        // отправителя в самом горячем потоке стоит лишнего вызова.
        var fmt = _waveFormat;
        if (fmt == null) return;

        TrackPacketSize(bytesRecorded);

        try
        {
            int bytesPerSample = Math.Max(1, fmt.BitsPerSample / 8);
            int frames = bytesRecorded / (fmt.Channels * bytesPerSample);
            if (frames == 0) return;

            // 1) Байты -> float (межленточные сэмплы в исходном формате)
            if (_decoded.Length < frames * fmt.Channels)
                _decoded = new float[frames * fmt.Channels];
            DecodeToFloat(packet, fmt, _decoded, frames);

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
            //    Процессор держит постоянную задержку (~20 мс — столько у
            //    rnnoise.dll) и всегда возвращает ровно outFrames, поэтому буфер
            //    не раздувается. Очередь внутри переваривает пакет целиком, так
            //    что длина пакета на результат не влияет.
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
    /// Пакет — это всё, что накопилось в буфере WASAPI к моменту пробуждения
    /// потока, поэтому в норме он равен периоду устройства (сейчас это буфер
    /// <see cref="AudioEngineDefaults.InputBufferMilliseconds"/> мс), а не 10 мс.
    /// Если обработка хоть раз отстала, пакет растёт — и это уже не задержка, а
    /// разрыв: часть звука уходит в следующий пакет позже, чем остальная.
    ///
    /// Проверка нужна ещё и потому, что раньше NAudio на каждый пакет выделял
    /// <c>new byte[bytesAvailable]</c>, и такой пакет за 85 КБ уезжал в LOH,
    /// который собирается только в gen2. Сейчас буфер отдаётся без копирования,
    /// но сам факт отставания потока никуда не делся и должен быть виден.
    /// </summary>
    private void TrackPacketSize(int bytes)
    {
        // Нормальный пакет = буфер, который мы запросили у устройства.
        int periodBytes = (int)Math.Max(1L,
            _bytesPerFrame * (long)AudioEngineDefaults.InputBufferMilliseconds * SampleRate / 1000);
        double bytesPerMs = _bytesPerFrame * (double)SampleRate / 1000.0;

        if (bytes <= periodBytes * 2)
        {
            // Пакет в норме. Сбрасываем и максимум, и флаг просрочки: иначе
            // предупреждение о всплеске больше не сработает до конца работы
            // источника, а первый же пакет (он всегда «новый максимум») ругался бы
            // на нормальный размер.
            _maxPacketBytes = bytes;
            _warnedBacklog = false;
            return;
        }

        if (bytes > _maxPacketBytes)
        {
            _maxPacketBytes = bytes;
            _logger.LogWarning(
                "Strip «{Strip}»: пакет {Bytes} байт ({Ms:F1} мс) при норме {Period:F1} мс — " +
                "поток захвата отстаёт",
                _model.Name, bytes, bytes / bytesPerMs, periodBytes / bytesPerMs);
        }

        // Просрочка: во сколько раз пакет больше того, что движок отдаёт за раз.
        // WASAPI сообщает накопление точнее (GetCurrentPadding), но это лишний вызов
        // в аудиопотоке, поэтому грубая оценка по размеру пакета достаточна.
        if (bytes > periodBytes * 4 && !_warnedBacklog)
        {
            _warnedBacklog = true;
            _logger.LogWarning(
                "Strip «{Strip}»: пакет вырос до {Bytes} байт (норма {Normal}) — " +
                "буфер WASAPI не успевает опустошаться, в звуке будут разрывы",
                _model.Name, bytes, periodBytes);
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
    private static void DecodeToFloat(ReadOnlySpan<byte> src, WaveFormat fmt, float[] dst, int frames)
    {
        int channels = fmt.Channels;
        int byteCount = src.Length;
        int total = frames * channels;
        if (total > dst.Length) total = dst.Length;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int samples = Math.Min(total, byteCount / 4);
            if (samples > 0) src.Slice(0, samples * 4).CopyTo(MemoryMarshal.AsBytes(dst.AsSpan(0, samples)));
            if (samples < total) Array.Clear(dst, samples, total - samples);
            return;
        }

        switch (fmt.BitsPerSample)
        {
            case 16:
            {
                int samples = Math.Min(total, byteCount / 2);
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt16(src.Slice(i * 2, 2)) / 32768f;
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
                    dst[i] = BitConverter.ToInt32(src.Slice(i * 4, 4)) / 2147483648f;
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
