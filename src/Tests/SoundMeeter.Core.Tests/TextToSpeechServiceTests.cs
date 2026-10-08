using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.TextToSpeech;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Модуль синтеза речи (SM-E01) целиком: включение, очередь, дроссель, выбор
/// голоса и доставка в микшер.
///
/// SAPI и WASAPI заменены заглушками намеренно. Проверяются решения модуля — что
/// он делает и в каком порядке, — а не работа синтезатора Windows: для неё есть
/// <see cref="Pcm16MonoStreamTests"/> и ручная проверка прямо из окна настроек.
/// </summary>
public class TextToSpeechServiceTests
{
    private const string Target = "strip-tts";
    private const int SampleRate = 48000;
    private const int Channels = 2;

    /// <summary>Синтезатор-заглушка: отдаёт заранее заданное число порций и запоминает вызовы.</summary>
    private sealed class FakeSpeechEngine : ITtsSpeechEngine
    {
    /// <summary>
    /// Заслон синтеза. Закрыт — озвучка ждёт. Нужен там, где важно поймать
    /// мгновение «сейчас говорится»: без него фраза успевает закончиться, пока
    /// проверка смотрит на состояние.
    /// </summary>
    public ManualResetEventSlim Gate { get; } = new(true);

        public bool IsAvailable { get; set; } = true;
        public string UnavailableReason => IsAvailable ? "" : "нет синтеза";

        public List<(string Text, TtsSpeechOptions Options)> Spoken { get; } = new();

        /// <summary>Сколько секунд звука отдаёт одна фраза.</summary>
        public double SecondsPerUtterance { get; set; } = 0.1;

        /// <summary>Задержать конец фразы — нужно, чтобы поймать состояние «озвучивается».</summary>
        public TimeSpan SpeakDuration { get; set; }

        public void Speak(string text, TtsSpeechOptions options, Action<float[]> onAudio,
            CancellationToken cancellationToken)
        {
            Spoken.Add((text, options));
            Gate.Wait(cancellationToken);

            // Отдаём фразу целиком, как это делает настоящий синтезатор: темп
            // отдачи задаёт не синтез, а источник, и он заметно быстрее реального
            // времени. На этом и строится проверка «следующая фраза ждёт».
            var block = new float[SampleRate / 10];
            Array.Fill(block, 0.5f);

            int portions = (int)Math.Max(1, Math.Round(SecondsPerUtterance * 10));
            for (int i = 0; i < portions && !cancellationToken.IsCancellationRequested; i++)
            {
                onAudio(block);
                if (SpeakDuration > TimeSpan.Zero) Thread.Sleep(SpeakDuration);
            }
        }

        public void Dispose() => Gate.Dispose();
    }

    /// <summary>Каталог голосов с фиксированным списком — SAPI в проверке не участвует.</summary>
    private sealed class FakeVoiceCatalog : ITtsVoiceCatalog
    {
        public List<TtsVoice> Installed { get; } =
        [
            new("Microsoft Zira", "en-US", "Female", ""),
            new("Microsoft Irina", "ru-RU", "Female", ""),
            new("Microsoft Pavel", "ru-RU", "Male", ""),
        ];

        public bool IsAvailable => Installed.Count > 0;
        public string UnavailableReason => "";
        public IReadOnlyList<TtsVoice> Voices => Installed;

        public TtsVoice? Find(string? name) => Installed.FirstOrDefault(
            voice => string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase));

        public TtsVoice? Resolve(string? preferred) =>
            Find(preferred) ?? Installed.FirstOrDefault(voice => voice.IsRussian) ?? Installed.FirstOrDefault();
    }

    /// <summary>Диспетчер выполняет сразу: в проверках UI-потока нет, а порядок событий должен совпадать.</summary>
    private sealed class ImmediateDispatcher : IDispatcherService
    {
        public bool HasThreadAccess => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>Считает сохранения: командой !ttsvoice голос пишется мимо кнопки «Применить».</summary>
    private sealed class CountingSettingsService : ISettingsService
    {
        public CountingSettingsService(AppSettings settings) => Settings = settings;

        public AppSettings Settings { get; }
        public int Saves { get; private set; }

        public void Save() => Saves++;
    }

    private sealed class Harness : IDisposable
    {
        public Harness(bool engineAvailable = true)
        {
            Engine = new FakeAudioEngine();
            Engine.FakeInputs.Add(new InputChannelModel
            {
                Id = Target,
                Name = "TTS",
                ChannelName = "TTS",
                IsGenerated = true,
                IsAvailable = true,
            });

            Speech = new FakeSpeechEngine { IsAvailable = engineAvailable };
            Voices = new FakeVoiceCatalog();
            Source = new ManualTtsMessageSource();
            SettingsService = new CountingSettingsService(new AppSettings());

            Service = new TextToSpeechService(Engine, Speech, Voices, Source, SettingsService,
                new ImmediateDispatcher());
        }

        public FakeAudioEngine Engine { get; }
        public FakeSpeechEngine Speech { get; }
        public FakeVoiceCatalog Voices { get; }
        public ManualTtsMessageSource Source { get; }
        public CountingSettingsService SettingsService { get; }
        public TextToSpeechService Service { get; }

        public AppSettings Settings => SettingsService.Settings;

        /// <summary>
        /// Задержать озвучку: уже начатая фраза будет ждать в синтезаторе. Нужно,
        /// чтобы наполнить очередь не дожидаясь проигрывания, — иначе результат
        /// зависит от того, успел ли поток вычеркнуть реплику до следующего нажатия.
        /// </summary>
        public void HoldSpeaking() => Speech.Gate.Reset();

        public void ReleaseSpeaking() => Speech.Gate.Set();

        /// <summary>Включает модуль с работающим микшером и указанными пределами.</summary>
        public void Enable(string targetInputId = Target, int queue = 10, int perMinute = 5)
        {
            Engine.AcceptAudio = true;
            Settings.TextToSpeech = new TextToSpeechSettings
            {
                Enabled = true,
                TargetInputId = targetInputId,
                DefaultVoice = "Microsoft Irina",
                MaxQueueLength = queue,
                MaxMessagesPerMinute = perMinute,
            };
            Service.Restore();
        }

        /// <summary>Дожидается, пока модуль договорит всё, что стояло в очереди.</summary>
        public void WaitUntilIdle(int timeoutMs = 5000)
        {
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                            (long)(timeoutMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

            while (Speech.Spoken.Count == 0 && !Service.IsSpeaking)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline) return;
                Thread.Sleep(5);
            }

            while (Service.IsSpeaking)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline) return;
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            // Заслон мог остаться закрытым: без сброса поток озвучки повис бы на нём
            // и выключение модуля ждало бы своего таймаута.
            ReleaseSpeaking();
            Service.Dispose();
        }
    }

    /// <summary>Ждёт выполнения условия, но не дольше указанного — чтобы проверка падала, а не висела.</summary>
    private static void SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        while (!condition() && System.Diagnostics.Stopwatch.GetTimestamp() < deadline) Thread.Sleep(5);
    }

    #region Включение

    [Fact]
    public void ADisabledModuleAcceptsNothing()
    {
        using var harness = new Harness();

        Assert.False(harness.Service.SubmitMessage("viewer", "!tts привет"));
        Assert.Empty(harness.Speech.Spoken);
    }

    [Fact]
    public void EnablingStartsTheSourceAndTheWorker()
    {
        using var harness = new Harness();

        harness.Enable();

        Assert.True(harness.Service.IsEnabled);
        Assert.True(harness.Source.IsRunning);
    }

    [Fact]
    public void DisablingStopsTheSourceAndTheWorker()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Service.Apply(new TextToSpeechSettings { Enabled = false }, saveNow: false);

        Assert.False(harness.Service.IsEnabled);
        Assert.False(harness.Source.IsRunning);

        harness.Service.SubmitMessage("viewer", "!tts привет");
        Assert.Empty(harness.Speech.Spoken);
    }

    /// <summary>
    /// Источник отдаёт то же, что отдаст чат Twitch, — тем же самым путём. Проверка
    /// нужна, чтобы подключение к чату нельзя было сделать в обход модуля.
    /// </summary>
    [Fact]
    public void MessagesFromTheSourceGoThroughTheSamePathAsTypedOnes()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Source.Publish("viewer", "!tts привет стример");
        harness.WaitUntilIdle();

        Assert.Single(harness.Speech.Spoken);
        Assert.Equal("привет стример", harness.Speech.Spoken[0].Text);
        Assert.NotEmpty(harness.Engine.PushedAudio);
        Assert.All(harness.Engine.PushedAudio, push => Assert.Equal(Target, push.InputId));
    }

    [Fact]
    public void AStoppedSourceAcceptsNothing()
    {
        using var harness = new Harness();
        harness.Enable();
        harness.Source.Stop();

        Assert.False(harness.Source.Publish("viewer", "!tts привет"));
    }

    /// <summary>
    /// Без синтеза в системе модуль не поднимается: иначе он принимал бы реплики
    /// и молчал, а пользователь искал бы причину в настройках.
    /// </summary>
    [Fact]
    public void AnEngineWithoutSynthesisKeepsTheModuleOff()
    {
        using var harness = new Harness(engineAvailable: false);
        harness.Settings.TextToSpeech = new TextToSpeechSettings { Enabled = true };

        harness.Service.Restore();

        Assert.False(harness.Service.SubmitMessage("viewer", "!tts привет"));
        Assert.Empty(harness.Speech.Spoken);
        Assert.False(harness.Service.IsStatusOk);
    }

    #endregion

    #region Когда берётся следующая реплика

    /// <summary>
    /// Ключевое свойство: следующая реплика начинает синтезироваться не сразу, а
    /// когда предыдущая ДОЗВУЧАЛА.
    ///
    /// Синтезатор отдаёт фразу целиком за доли секунды — на этой машине в 44 раза
    /// быстрее реального времени. Если ждать только возврата из синтеза, вторая
    /// реплика ложилась бы в буфер поверх первой, и к концу стрима модуль накопил
    /// бы минуты звука при «пустой» очереди.
    /// </summary>
    [Fact]
    public void TheNextPhraseWaitsForThePreviousOneToBeHeard()
    {
        using var harness = new Harness();
        harness.Enable(queue: 10, perMinute: 100);

        // Заглушка отдаёт 2 секунды звука и держит их в буфере источника, как
        // настоящий синтезатор.
        harness.Speech.SecondsPerUtterance = 2.0;

        harness.Service.SubmitMessage("first", "!tts первая");
        harness.Service.SubmitMessage("second", "!tts вторая");

        SpinUntil(() => harness.Speech.Spoken.Count >= 1, TimeSpan.FromSeconds(10));

        // Пока в буфере остаётся недозвучавшее, вторая реплика не начинается.
        // Проверка по условию, а не по паузе: под нагрузкой пауза могла бы истекчь
        // раньше, чем что-то случится, и проверка прошла бы вхолостую.
        SpinUntil(() => harness.Engine.BufferedAudioSeconds < 1.0, TimeSpan.FromSeconds(10));
        Assert.Single(harness.Speech.Spoken);

        SpinUntil(() => harness.Speech.Spoken.Count >= 2, TimeSpan.FromSeconds(10));
        Assert.Equal(["первая", "вторая"], harness.Speech.Spoken.Select(phrase => phrase.Text));
    }

    /// <summary>
    /// Пока фраза доигрывает, модуль обязан считать, что говорит: синтез к этому
    /// моменту давно кончился, а голос ещё на экране. Иначе кнопка «стоп» и строка
    /// состояния врали бы именно тогда, когда ими пользуются.
    /// </summary>
    [Fact]
    public void TheModuleStillCountsAsSpeakingWhileTheBufferDrains()
    {
        using var harness = new Harness();
        harness.Enable();
        harness.Speech.SecondsPerUtterance = 2.0;

        harness.Service.SubmitMessage("viewer", "!tts длинная фраза");

        SpinUntil(() => harness.Speech.Spoken.Count >= 1, TimeSpan.FromSeconds(10));
        Thread.Sleep(150);
        Assert.True(harness.Service.IsSpeaking);

        SpinUntil(() => !harness.Service.IsSpeaking, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// «Стоп» обязан замолчать. Отмены одного синтеза мало: фраза уже лежит в
    /// буфере источника и доиграла бы сама.
    /// </summary>
    [Fact]
    public void StoppingAlsoSilencesWhatTheSynthesizerAlreadyDelivered()
    {
        using var harness = new Harness();
        harness.Enable();
        harness.Speech.SecondsPerUtterance = 5.0;

        harness.Service.SubmitMessage("viewer", "!tts очень длинная фраза");
        SpinUntil(() => harness.Speech.Spoken.Count >= 1, TimeSpan.FromSeconds(10));
        SpinUntil(() => harness.Engine.BufferedAudioSeconds > 1.0, TimeSpan.FromSeconds(10));

        harness.Service.StopSpeaking();

        Assert.Equal(0, harness.Engine.BufferedAudioSeconds);
        SpinUntil(() => !harness.Service.IsSpeaking, TimeSpan.FromSeconds(10));
        Assert.False(harness.Service.IsSpeaking);
    }

    #endregion

    #region Что будит озвучку

    /// <summary>
    /// Реплика будит поток, спящий с момента включения модуля. Никакого таймера и
    /// никакого опроса нет: без сообщения модуль и не тратит ничего.
    /// </summary>
    [Fact]
    public void TheVeryFirstMessageIsPickedUpImmediately()
    {
        using var harness = new Harness();
        harness.Enable();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        harness.Service.SubmitMessage("viewer", "!tts первая реплика");

        // Извлечение из очелки — микросекунды; ждать нужно только синтез.
        SpinUntil(() => harness.Speech.Spoken.Count >= 1, TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.Equal("первая реплика", harness.Speech.Spoken[0].Text);

        // Извлечение из очереди не ждёт своего часа: поток спит и просыпается от
        // пульса, а не от таймера. Пять секунд — с запасом на загрузку машины, при
        // которой весь этот тест вообще мог бы не запуститься.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Реплика озвучена только через {stopwatch.ElapsedMilliseconds} мс — очередь ждала таймера");
    }

    /// <summary>
    /// Модуль, включённый при старте приложения БЕЗ доступного синтеза (не
    /// установлен языковой пакет), должен заговорить, как только синтез появится.
    ///
    /// Раньше поток поднимался только на переходе выкл→вкл, поэтому модуль оставался
    /// немым навсегда, а строка состояния рапортовала «работает, ждёт сообщений».
    /// </summary>
    [Fact]
    public void TheModuleStartsTalkingOnceSynthesisBecomesAvailable()
    {
        using var harness = new Harness(engineAvailable: false);
        harness.Settings.TextToSpeech = new TextToSpeechSettings { Enabled = true };

        harness.Service.Restore();
        Assert.False(harness.Source.IsRunning);       // синтеза нет — потока нет

        // Пользователь поставил языковой пакет и нажал «Применить».
        harness.Speech.IsAvailable = true;
        harness.Service.Apply(harness.Settings.TextToSpeech, saveNow: false);

        Assert.True(harness.Source.IsRunning);

        harness.Engine.AcceptAudio = true;
        harness.Settings.TextToSpeech.TargetInputId = Target;
        harness.Service.SubmitMessage("viewer", "!tts привет");
        harness.WaitUntilIdle();

        Assert.Single(harness.Speech.Spoken);
    }

    /// <summary>
    /// Постановка в очередь сама подтверждает, что поток жив, и поднимает его, если
    /// его нет. Проверяется на состоянии, которое реально возникает: модуль включили
    /// без синтеза (потока нет), синтез появился — и первая же реплика обязана
    /// заговорить, даже если никто не нажимал «Применить».
    ///
    /// Иначе реплики копились бы в очереди молча — отказ, который невозможно
    /// отличить от «модуль выключен», не читая журнал.
    /// </summary>
    [Fact]
    public void TheFirstMessageItselfRaisesTheMissingWorker()
    {
        using var harness = new Harness(engineAvailable: false);
        harness.Settings.TextToSpeech = new TextToSpeechSettings
        {
            Enabled = true,
            TargetInputId = Target,
        };
        harness.Engine.AcceptAudio = true;
        harness.Service.Restore();

        Assert.False(harness.Source.IsRunning);       // потока нет

        harness.Speech.IsAvailable = true;           // синтез появился

        harness.Service.SubmitMessage("viewer", "!tts привет");
        harness.WaitUntilIdle();

        Assert.True(harness.Source.IsRunning);
        Assert.Single(harness.Speech.Spoken);
    }

    [Fact]
    public void AStoppedModuleDoesNotResurrectItsWorkerOnAReplika()
    {
        using var harness = new Harness();
        harness.Enable();
        harness.Service.Apply(new TextToSpeechSettings { Enabled = false }, saveNow: false);

        harness.Service.SubmitMessage("viewer", "!tts привет");
        harness.WaitUntilIdle();

        Assert.False(harness.Source.IsRunning);
        Assert.Empty(harness.Speech.Spoken);
    }

    #endregion

    #region Голоса

    [Fact]
    public void TheVoiceOfTheAuthorIsUsedInsteadOfTheDefaultOne()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Pavel");
        harness.Service.SubmitMessage("moderator", "!tts я модератор");
        harness.WaitUntilIdle();

        Assert.Equal("Microsoft Pavel", harness.Speech.Spoken[^1].Options.VoiceName);
    }

    [Fact]
    public void AUserWithoutTheirOwnVoiceSpeaksWithTheDefault()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Pavel");
        harness.Service.SubmitMessage("viewer", "!tts я зритель");
        harness.WaitUntilIdle();

        Assert.Equal("Microsoft Irina", harness.Speech.Spoken[^1].Options.VoiceName);
    }

    /// <summary>
    /// Голос выбирается один раз и должен пережить перезапуск приложения, поэтому
    /// команда сохраняет его сразу, а не по кнопке «Применить».
    /// </summary>
    [Fact]
    public void TheChosenVoiceIsSavedImmediately()
    {
        using var harness = new Harness();
        harness.Enable();

        int savesBefore = harness.SettingsService.Saves;
        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Pavel");

        Assert.True(harness.SettingsService.Saves > savesBefore);
        Assert.Contains(harness.Settings.TextToSpeech.UserVoices,
            pair => pair.User == "moderator" && pair.Voice == "Microsoft Pavel");
    }

    [Fact]
    public void ChoosingTheVoiceTwiceReplacesTheOldOne()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Pavel");
        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Zira");

        var voices = harness.Settings.TextToSpeech.UserVoices.Where(p => p.User == "moderator").ToList();
        Assert.Single(voices);
        Assert.Equal("Microsoft Zira", voices[0].Voice);
    }

    [Fact]
    public void LoginsAreComparedWithoutCaseSensitivity()
    {
        using var harness = new Harness();
        harness.Enable();

        harness.Service.SubmitMessage("Moderator", "!ttsvoice Microsoft Pavel");
        harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Zira");

        Assert.Single(harness.Settings.TextToSpeech.UserVoices);
    }

    /// <summary>
    /// Без явного выбора модуль говорит по-русски: русский голос берётся раньше
    /// любого другого, иначе стример услышал бы русскую фразу английским голосом и
    /// решил, что модуль сломан.
    /// </summary>
    [Fact]
    public void WithoutAnExplicitChoiceTheVoiceIsRussian()
    {
        using var harness = new Harness();
        harness.Enable();

        Assert.Equal("Microsoft Irina", harness.Service.ResolveVoiceFor("viewer")?.Name);
    }

    [Fact]
    public void AnUnknownVoiceFromChatIsNotRemembered()
    {
        using var harness = new Harness();
        harness.Enable();

        Assert.False(harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Hal 9000"));
        Assert.Empty(harness.Settings.TextToSpeech.UserVoices);
    }

    #endregion

    #region Дроссель и очередь

    /// <summary>«!tts» в цикле от одного аккаунта иначе занимает эфир на всё время стрима.</summary>
    [Fact]
    public void OneUserCannotFloodTheStream()
    {
        using var harness = new Harness();
        harness.Enable(perMinute: 3);
        harness.Speech.SpeakDuration = TimeSpan.FromMilliseconds(20);

        int accepted = 0;
        for (int i = 0; i < 10; i++)
        {
            if (harness.Service.SubmitMessage("spammer", $"!tts реплика {i}")) accepted++;
        }

        Assert.Equal(3, accepted);
    }

    [Fact]
    public void TheThrottleIsPerUserNotGlobal()
    {
        using var harness = new Harness();
        harness.Enable(perMinute: 1);

        Assert.True(harness.Service.SubmitMessage("first", "!tts раз"));
        Assert.False(harness.Service.SubmitMessage("first", "!tts два"));
        Assert.True(harness.Service.SubmitMessage("second", "!tts раз"));
    }

    /// <summary>
    /// Иначе модератор, перебирающий себе голоса, одной серией команд заблочил бы
    /// собственную озвучку до конца минуты.
    /// </summary>
    [Fact]
    public void TheVoiceCommandDoesNotConsumeTheSpeakingRate()
    {
        using var harness = new Harness();
        harness.Enable(perMinute: 1);

        for (int i = 0; i < 5; i++) harness.Service.SubmitMessage("moderator", "!ttsvoice Microsoft Pavel");

        Assert.True(harness.Service.SubmitMessage("moderator", "!tts привет"));
    }

    /// <summary>
    /// Очередь ограничена, и при переполнении выбрасывается начало, а не конец:
    /// через минуту пользователь должен услышать свежее, а не то, что было пятью
    /// минутами раньше. Проверяется не длина, а СОСТАВ — счётчик прошёл бы и при
    /// обратном порядке отбрасывания.
    /// </summary>
    /// <summary>
    /// Переполненная очередь отбрасывает середину, а не то, что уже говорится, и
    /// не самое свежее.
    ///
    /// Именно так и должно быть: начатую фразу нельзя вырезать (её уже слышно), а
    /// последние реплики — самое ценное, через минуту они ещё актуальны. Проверяется
    /// состав, а не длина: счётчик прошёл бы и при обратном порядке отбрасывания.
    /// </summary>
    [Fact]
    public void AFullQueueDropsTheOldestWaitingMessagesButNotTheOneBeingSpoken()
    {
        using var harness = new Harness();
        harness.Enable(queue: 3, perMinute: 100);
        harness.HoldSpeaking();

        for (int i = 0; i < 6; i++) harness.Service.SubmitMessage("viewer", $"!tts реплика {i}");

        // Реплика 0 уже извлечена потоком и ждёт в синтезаторе, три оставшиеся
        // места заняты хвостом очереди.
        Assert.Equal(3, harness.Service.QueueLength);

        harness.ReleaseSpeaking();
        harness.WaitUntilIdle();

        Assert.Equal(["реплика 0", "реплика 3", "реплика 4", "реплика 5"],
            harness.Speech.Spoken.Select(phrase => phrase.Text));
    }

    [Fact]
    public void TheQueueCanBeCleared()
    {
        using var harness = new Harness();
        harness.Enable(queue: 10, perMinute: 100);
        harness.HoldSpeaking();

        harness.Service.SubmitMessage("viewer", "!tts первая");
        harness.Service.SubmitMessage("viewer", "!tts вторая");
        harness.Service.SubmitMessage("viewer", "!tts третья");
        Assert.Equal(2, harness.Service.QueueLength);

        harness.Service.ClearQueue();
        Assert.Equal(0, harness.Service.QueueLength);

        // Очищенное не озвучивается: иначе кнопка «очистить очередь» была бы
        // просто кнопкой «подождать».
        harness.ReleaseSpeaking();
        harness.WaitUntilIdle();
        Assert.Equal(["первая"], harness.Speech.Spoken.Select(phrase => phrase.Text));
    }

    #endregion

    #region Куда уходит голос

    /// <summary>
    /// Проверка «можно ли» идёт ДО синтеза: иначе модуль тратил бы секунды CPU на
    /// фразу, которую никто не услышит.
    /// </summary>
    [Fact]
    public void SpeechIsNotSynthesizedWhenThereIsNowhereToPutIt()
    {
        using var harness = new Harness();
        harness.Enable(targetInputId: "");

        harness.Service.SubmitMessage("viewer", "!tts привет");
        harness.WaitUntilIdle();

        Assert.Empty(harness.Speech.Spoken);
        Assert.False(harness.Service.IsStatusOk);
    }

    [Fact]
    public void TheTargetIsFoundById()
    {
        using var harness = new Harness();
        harness.Enable();

        Assert.True(harness.Service.CanSpeakNow());
        Assert.Equal("TTS", harness.Service.TargetTitle);
    }

    /// <summary>
    /// Если стрип пересоздали, его Id не совпадёт, но DeviceId у того же устройства
    /// останется. Без этого голос замолчал бы после любого обновления настроек.
    /// </summary>
    [Fact]
    public void TheTargetIsFoundByDeviceIdWhenTheStripWasRecreated()
    {
        using var harness = new Harness();
        harness.Engine.AcceptAudio = true;
        harness.Engine.FakeInputs[0].DeviceId = "device-1";
        harness.Settings.TextToSpeech = new TextToSpeechSettings
        {
            Enabled = true,
            TargetInputId = "strip-that-no-longer-exists",
            TargetInputDeviceId = "device-1",
        };
        harness.Service.Restore();

        Assert.True(harness.Service.CanSpeakNow());
    }

    [Fact]
    public void AStripThatVanishedSilencesTheModuleInsteadOfSpeakingToNowhere()
    {
        using var harness = new Harness();
        harness.Enable(targetInputId: "strip-that-no-longer-exists");

        harness.Service.SubmitMessage("viewer", "!tts привет");
        harness.WaitUntilIdle();

        Assert.Empty(harness.Speech.Spoken);
    }

    /// <summary>
    /// Остановленный микшер — тоже некуда говорить. Голос должен пропасть, а не
    /// копиться в очереди источника.
    /// </summary>
    [Fact]
    public void AStoppedMixerIsNowhereToSpeakTo()
    {
        using var harness = new Harness();
        harness.Enable();
        harness.Engine.Stop();

        Assert.False(harness.Service.CanSpeakNow());
    }

    #endregion

    #region Остановка

    [Fact]
    public void StoppingKeepsTheQueueButEndsTheCurrentPhrase()
    {
        using var harness = new Harness();
        harness.Enable(queue: 10, perMinute: 100);
        harness.Speech.SecondsPerUtterance = 30.0;
        harness.Speech.SpeakDuration = TimeSpan.FromMilliseconds(20);

        harness.Service.SubmitMessage("first", "!tts длинная фраза");
        harness.Service.SubmitMessage("second", "!tts вторая фраза");

        SpinUntil(() => harness.Speech.Spoken.Count >= 1, TimeSpan.FromSeconds(10));
        harness.Service.StopSpeaking();

        // Вторая не потерялась: очередь переживает остановку, и её голос ещё ждёт.
        SpinUntil(() => harness.Speech.Spoken.Count >= 2, TimeSpan.FromSeconds(10));
        Assert.Contains(harness.Speech.Spoken, phrase => phrase.Text == "вторая фраза");
    }

    #endregion
}
