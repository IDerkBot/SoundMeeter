using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Синтез речи через SAPI — системный синтезатор Windows.
///
/// Почему SAPI, а не своя модель или облако: модуль должен работать на стриме
/// без интернета и без ключей, а русские голоса в Windows уже есть. SAPI даёт
/// и то, и другое бесплатно; плата — качество, но для зачитки коротких реплик
/// из чата его достаточно.
///
/// Формат выдачи — 48 кГц/стерео/float: ровно то, что ждёт кольцо микшера.
/// Частоту, которую SAPI реально согласен отдать, угадывать приходится (список
/// поддерживаемых форматов зависит от установленных движков речи), поэтому
/// <see cref="Speak"/> перебирает варианты и запоминает удавшийся.
/// </summary>
public sealed class SapiSpeechEngine : ITtsSpeechEngine
{
    /// <summary>Частота, нужная кольцу микшера. Совпадает с <c>InputSource.SampleRate</c>.</summary>
    private const int TargetRate = Pcm16MonoStream.TargetRate;

    /// <summary>Порция выдачи, кадров (10 мс). Мельче — только лишние вызовы.</summary>
    private const int ChunkFrames = Pcm16MonoStream.ChunkFrames;

    /// <summary>
    /// Частоты, которые пробуем попросить у SAPI, по убыванию предпочтения.
    /// 48 кГц — идеал, без ресемплинга; 24 кГц делится на 48 нацело, поэтому
    /// даже при отказе от 48 кГц интерполяция попадает точно в исходные отсчёты.
    /// </summary>
    private static readonly int[] PreferredRates = [48000, 24000, 16000];

    /// <summary>
    /// Потолок ожидания синтеза. Нужен на случай, если SAPI потеряет событие о
    /// завершении: без него фраза держала бы очередь озвучки до бесконечности.
    /// </summary>
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger = AppLog.For<SapiSpeechEngine>();
    private readonly Lazy<string> _probe;
    private readonly object _rateLock = new();

    /// <summary>Частота выдачи, удавшаяся в прошлый раз (0 — ещё не пробовали).</summary>
    private int _sourceRate;

    public SapiSpeechEngine() =>
        _probe = new Lazy<string>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => _probe.Value.Length == 0;

    public string UnavailableReason => _probe.Value;

    /// <summary>
    /// Проверка доступности SAPI. Конструктор <see cref="SpeechSynthesizer"/>
    /// ничего не печатает и не открывает устройство вывода, поэтому проверка
    /// безопасна и выполняется один раз на процесс.
    /// </summary>
    private string Probe()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            if (!synthesizer.GetInstalledVoices().Any(installed => installed.Enabled))
                return Loc.Get("Sm.Tts.NoVoices");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAPI недоступен для синтеза: {Message}", ex.Message);
            return Loc.Get("Sm.Tts.EngineUnavailable", ex.Message);
        }

        return "";
    }

    public void Speak(string text, TtsSpeechOptions options, Action<float[]> onAudio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onAudio);

        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (cancellationToken.IsCancellationRequested) return;

        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Rate = options.SapiRate;
        synthesizer.Volume = options.SapiVolume;
        ApplyVoice(synthesizer, options.VoiceName);

        var stream = AttachOutput(synthesizer, onAudio);

        // PromptBuilder, а не SpeakAsync(string): он экранирует разметку, иначе
        // сообщение вида «<3» или «a & b» отравило бы фразу разметкой SSML.
        var prompt = new PromptBuilder
        {
            // Язык правил произношения берём у голоса, а не у системы: иначе
            // русская фраза, прочитанная английским голосом, нарезалась бы по
            // английским правилам.
            Culture = synthesizer.Voice?.Culture ?? System.Globalization.CultureInfo.CurrentUICulture,
        };
        prompt.AppendText(text);

        Exception? failure = null;
        using var finished = new ManualResetEventSlim(false);

        synthesizer.SpeakCompleted += (_, args) =>
        {
            if (args.Error != null) failure = args.Error;
            finished.Set();
        };

        // Prompt нужен для отмены: SAPI умеет отменить конкретный запрос, и без
        // ссылки на него отмена затронула бы всю очередь синтеза.
        var queued = synthesizer.SpeakAsync(prompt);

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                synthesizer.SpeakAsyncCancel(queued);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Отмена синтеза не принята: {Message}", ex.Message);
            }
        });

        if (!finished.Wait(SynthesisTimeout) && !cancellationToken.IsCancellationRequested)
            throw new TimeoutException("SAPI не сообщил о завершении синтеза");

        // Хвост, который SAPI успел записать, но не успел дослать целой порцией.
        stream.Complete();

        // Отмена — не ошибка: то, что успело прозвучать к этому моменту, уже в
        // кольце, и ругаться на это в журнале незачем.
        if (failure is not null && !cancellationToken.IsCancellationRequested)
            throw new InvalidOperationException($"Ошибка синтеза: {failure.Message}", failure);
    }

    /// <summary>
    /// Выбирает голос. Неизвестное имя молча игнорируем: голос мог быть удалён
    /// из системы после того, как попал в настройки или в сообщение чата, и
    /// падать на этом нельзя — SAPI возьмёт свой голос по умолчанию.
    /// </summary>
    private void ApplyVoice(SpeechSynthesizer synthesizer, string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName)) return;

        try
        {
            synthesizer.SelectVoice(voiceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Голос «{Voice}» недоступен, используется голос SAPI по умолчанию: {Message}",
                voiceName, ex.Message);
        }
    }

    /// <summary>
    /// Подключает поток выдачи на частоте, которую умеет выбранный голос.
    ///
    /// Частоту берём не «на глаз», а из <see cref="System.Speech.Synthesis.VoiceInfo.SupportedAudioFormats"/>:
    /// SAPI не обязан отдавать любой стандартный формат, и молчаливая неудача
    /// выглядела бы как «голос говорит, а звука нет». Найденная частота
    /// запоминается — перебор стоит реального времени на каждой фразе.
    /// </summary>
    private Pcm16MonoStream AttachOutput(SpeechSynthesizer synthesizer, Action<float[]> sink)
    {
        Exception? lastFailure = null;
        int[] candidates;

        lock (_rateLock)
        {
            candidates = CandidateRates(synthesizer);

            foreach (int rate in candidates)
            {
                try
                {
                    var stream = new Pcm16MonoStream(rate, sink);
                    synthesizer.SetOutputToAudioStream(stream,
                        new SpeechAudioFormatInfo(rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

                    if (_sourceRate != rate)
                    {
                        _sourceRate = rate;
                        _logger.LogInformation("SAPI отдаёт речь в {Rate} Гц (требуется {Target})",
                            rate, TargetRate);
                    }

                    return stream;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    _logger.LogDebug(ex, "SAPI не согласен отдать {Rate} Гц: {Message}", rate, ex.Message);
                }
            }
        }

        // Причина отказа обязательна в сообщении: иначе по тексту нельзя понять,
        // это запрет формата, отсутствие аудиопотока или отказ драйвера.
        throw new InvalidOperationException(
            $"SAPI не поддерживает ни одну из частот выдачи ({string.Join(", ", candidates)} Гц): " +
            $"последняя ошибка — {lastFailure?.Message ?? "неизвестна"}", lastFailure);
    }

    /// <summary>
    /// Частоты к перебору: сперва удавшаяся в прошлый раз, затем поддержанные
    /// голосом в порядке предпочтения, затем весь список — на случай, если голос
    /// вообще не сообщил о своих форматах.
    /// </summary>
    private int[] CandidateRates(SpeechSynthesizer synthesizer)
    {
        var candidates = new List<int>();

        if (_sourceRate > 0) candidates.Add(_sourceRate);

        try
        {
            var supported = synthesizer.Voice?.SupportedAudioFormats;
            if (supported is { Count: > 0 })
            {
                bool IsUsable(SpeechAudioFormatInfo format) =>
                    format.EncodingFormat == EncodingFormat.Pcm &&
                    format.BitsPerSample == 16 &&
                    format.ChannelCount == 1;

                foreach (int preferred in PreferredRates)
                {
                    if (supported.Any(format => IsUsable(format) && format.SamplesPerSecond == preferred))
                        candidates.Add(preferred);
                }

                // Ни одна из предпочтительных не подошла — берём любой
                // одноканальный PCM16 формат, какой голос признаёт.
                foreach (var format in supported.Where(IsUsable))
                {
                    if (!candidates.Contains(format.SamplesPerSecond))
                        candidates.Add(format.SamplesPerSecond);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Список форматов голоса недоступен: {Message}", ex.Message);
        }

        foreach (int preferred in PreferredRates)
        {
            if (!candidates.Contains(preferred)) candidates.Add(preferred);
        }

        return candidates.ToArray();
    }

    public void Dispose() { }

}
