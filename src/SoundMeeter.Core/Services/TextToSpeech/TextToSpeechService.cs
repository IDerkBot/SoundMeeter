using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Модуль синтеза речи (SM-E01).
///
/// Собирает вместе три разные вещи, которые иначе пришлось бы связать в разметке:
///
/// * <b>включение</b> — как «сервер дока» в OBS: модуль живёт вместе с
///   приложением и выключается одним переключателем;
/// * <b>приём реплик</b> — источник сообщений плюс разбор команд
///   (<see cref="TtsChatCommandParser"/>), то есть ровно та часть, которая
///   сегодня работает вручную, а завтра будет работать от чата Twitch;
/// * <b>выдачу в микшер</b> — синтез и отправка сэмплов в целевой входной стрип.
///
/// Очередь и отдельный поток нужны по двум причинам. Первая: синтез занимает
/// столько же времени, сколько звучит фраза, поэтому одновременные реплики
/// физически не могут звучать вместе — их надо выстраивать в очередь. Вторая:
/// <see cref="ITtsSpeechEngine.Speak"/> синхронный и бьёт по SAPI, поэтому звать
/// его прямо из обработчика сообщения нельзя — обработчик источника должен
/// вернуться немедленно, иначе источник (а с ним и чтение чата) встанет.
///
/// Дроссель по пользователю обязателен: без него «<c>!tts</c>» в цикле от
/// одного аккаунта занимает эфир на всё время стрима.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly ITtsSpeechEngine _speech;
    private readonly ITtsVoiceCatalog _voices;
    private readonly ITtsMessageSource _source;
    private readonly ISettingsService _settings;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger _logger = AppLog.For<TextToSpeechService>();

    private readonly object _sync = new();
    private readonly Queue<TtsUtterance> _queue = new();
    private readonly Dictionary<string, Queue<DateTime>> _recentPerUser = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _lifetime;

    /// <summary>
    /// Отмена текущей фразы. Пишется из потока озвучки, читается из UI-потока и
    /// из обработчика сообщений, поэтому поле изменчивое: без этого окно настроек
    /// могло бы нажать «стоп» и не увидеть, что фраза уже закончилась.
    /// </summary>
    private volatile CancellationTokenSource? _speaking;

    private Thread? _worker;
    private bool _disposed;

    private volatile string _status = "";
    private volatile bool _statusOk = true;

    public TextToSpeechService(
        IAudioEngine engine,
        ITtsSpeechEngine speech,
        ITtsVoiceCatalog voices,
        ITtsMessageSource source,
        ISettingsService settings,
        IDispatcherService dispatcher)
    {
        _engine = engine;
        _speech = speech;
        _voices = voices;
        _source = source;
        _settings = settings;
        _dispatcher = dispatcher;

        _source.MessageReceived += OnMessage;
    }

    /// <summary>Живые настройки модуля (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public TextToSpeechSettings Settings => _settings.Settings.TextToSpeech;

    /// <summary>Модуль включён и принимает реплики.</summary>
    public bool IsEnabled => Settings.Enabled;

    /// <summary>
    /// Сейчас идёт звук: синтезируется фраза или уже отданный буфер доигрывается.
    ///
    /// Второе важно отдельно. Синтезатор отдаёт текст в десятки раз быстрее
    /// реального времени, поэтому «синтез идёт» становится ложью уже через доли
    /// секунды — голос ещё говорит, а модуль об этом не знает. Для интерфейса
    /// «озвучивается» значит именно «звук идёт или сейчас пойдёт».
    /// </summary>
    public bool IsSpeaking
    {
        get
        {
            if (_speaking is { IsCancellationRequested: false }) return true;
            return TryResolveTarget(out string targetId, out _) && _engine.GetBufferedSeconds(targetId) > 0;
        }
    }

    /// <summary>Сколько реплик ждёт очереди.</summary>
    public int QueueLength
    {
        get
        {
            lock (_sync) return _queue.Count;
        }
    }

    /// <summary>Список доступных голосов (для окна настроек).</summary>
    public IReadOnlyList<TtsVoice> Voices => _voices.Voices;

    /// <summary>Синтез в системе работает.</summary>
    public bool IsEngineAvailable => _speech.IsAvailable;

    /// <summary>Состояние изменилось — окно настроек обновит надписи.</summary>
    public event Action? StatusChanged;

    /// <summary>Статусная строка модуля. Пустая — пока нечего сообщать.</summary>
    public string Status => _status;

    /// <summary>Статус — это ошибка (красным), а не подтверждение.</summary>
    public bool IsStatusOk => _statusOk;

    /// <summary>
    /// Голос, который прозвучит при следующей фразе: заданный пользователем для
    /// этого автора, иначе голос по умолчанию. null, если в системе нет ни одного
    /// голоса.
    /// </summary>
    public TtsVoice? ResolveVoiceFor(string user)
    {
        var settings = Settings;
        var assigned = settings.UserVoices.FirstOrDefault(pair =>
            string.Equals(pair.User, user, StringComparison.OrdinalIgnoreCase));

        return _voices.Resolve(assigned?.Voice ?? settings.DefaultVoice);
    }

    /// <summary>Есть ли голос с таким именем — проверка для команды <c>!ttsvoice</c>.</summary>
    public bool IsKnownVoice(string name) => _voices.Find(name) is not null;

    /// <summary>
    /// Применить настройки из окна: включение, целевой канал, голоса и пределы.
    /// Перезапускает источник и поток озвучки только если это действительно
    /// потребовалось, иначе смена крутилки темпа роняла бы текущую фразу.
    /// </summary>
    public void Apply(TextToSpeechSettings updated, bool saveNow = true)
    {
        var live = Settings;
        bool disarming = live.Enabled && !updated.Enabled;

        live.Enabled = updated.Enabled;
        live.TargetInputId = updated.TargetInputId;
        live.TargetInputDeviceId = updated.TargetInputDeviceId;
        live.DefaultVoice = updated.DefaultVoice;
        live.Rate = updated.Rate;
        live.Volume = updated.Volume;
        live.SpeakCommand = updated.SpeakCommand;
        live.VoiceCommand = updated.VoiceCommand;
        live.AllowVoiceChange = updated.AllowVoiceChange;
        live.MaxQueueLength = updated.MaxQueueLength;
        live.MaxMessagesPerMinute = updated.MaxMessagesPerMinute;
        live.MaxMessageChars = updated.MaxMessageChars;
        live.UserVoices = updated.UserVoices.Select(pair => pair.Clone()).ToList();
        live.IgnoredUsers = updated.IgnoredUsers.ToList();

        if (disarming)
        {
            StopEverything(Loc.Get("Sm.Tts.Status.Off"));
        }
        else if (live.Enabled)
        {
            // Именно «а не только при включении», а на любое применение. Раньше
            // Restore звался лишь на переходе выкл→вкл, и модуль оставался
            // вечно немым в реальном случае: синтез был недоступен при старте
            // (не установлен языковой пакет), пользователь поставил голос и
            // нажал «Применить» — а поток озвучки так и не появился, при том что
            // строка состояния рапортовала «работает».
            Restore();
        }

        if (saveNow) _settings.Save();
        RefreshStatus();
    }

    /// <summary>
    /// Поднять модуль после запуска приложения. Молча выходит, если он выключен
    /// в настройках или синтез в системе недоступен: в обоих случаях это не
    /// ошибка, а состояние, и «чинить» его при старте незачем.
    /// </summary>
    public void Restore()
    {
        if (!Settings.Enabled) return;

        if (!_speech.IsAvailable)
        {
            _logger.LogWarning("Модуль TTS включён, но синтез недоступен: {Reason}", _speech.UnavailableReason);
            SetStatus(_speech.UnavailableReason, false);
            return;
        }

        StartEverything();
    }

    /// <summary>
    /// Поднимает поток озвучки, если его нет. Повторный вызов на живом потоке
    /// ничего не делает.
    ///
    /// На каждый горячий путь не ходит: сначала дешёвая проверка без замка, и уже
    /// только если потока действительно нет — подъём под замком.
    /// </summary>
    private void EnsureWorkerRunning()
    {
        if (_worker is { IsAlive: true }) return;
        if (_lifetime is { IsCancellationRequested: false }) return;

        if (IsEnabled && _speech.IsAvailable)
        {
            StartEverything();
            return;
        }

        // Модуль включён, но говорить нечем — синтеза в системе нет. Раньше эта
        // ситуация обнаруживалась только при старте приложения, и после установки
        // языкового пакета модуль оставался немым до перезапуска. Теперь о ней
        // известно сразу, и в журнале есть след.
        SetStatus(_speech.IsAvailable ? Loc.Get("Sm.Tts.Status.Off") : _speech.UnavailableReason, false);
    }

    /// <summary>Подключить источник, поднять поток озвучки и подписаться на канал.</summary>
    private void StartEverything()
    {
        lock (_sync)
        {
            if (_worker is { IsAlive: true }) return;

            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;

            _source.Start(Settings);

            _worker = new Thread(() => WorkerLoop(token))
            {
                IsBackground = true,
                Name = "SoundMeeter.TTS",
            };
            _worker.Start();
        }

        _logger.LogInformation("Модуль TTS включён, источник «{Source}»", _source.Name);
        SetStatus(Loc.Get("Sm.Tts.Status.Running"), true);
    }

    private void StopEverything(string status)
    {
        _speaking?.Cancel();

        Thread? worker;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            worker = _worker;
            lifetime = _lifetime;
            _worker = null;
            _lifetime = null;
            _queue.Clear();

            // Поток озвучки спит на Monitor.Wait, а отмена токена его не будит:
            // без пульса он проснулся бы только по таймауту Join, то есть выключение
            // модуля и выход из приложения тянули бы на три секунды каждый.
            Monitor.PulseAll(_sync);
        }

        _source.Stop();
        lifetime?.Cancel();

        if (worker is { IsAlive: true })
        {
            try
            {
                worker.Join(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Поток озвучки не завершился: {Message}", ex.Message);
            }
        }

        lifetime?.Dispose();
        _speaking?.Dispose();
        _speaking = null;

        _logger.LogInformation("Модуль TTS выключен");
        SetStatus(status, true);
    }

    /// <summary>
    /// Остановить текущую фразу. Очередь при этом сохраняется.
    ///
    /// Двухчастная операция, и обе части обязательны. Отмена синтеза без выброса
    /// буфера ничего бы не дала: SAPI отдаёт фразу целиком за доли секунды, так что
    /// к моменту нажатия «стоп» она уже целиком лежит в источнике и доиграет сама.
    /// Выброс буфера без отмены синтеза оставил бы синтезатор дописывать фразу,
    /// которую тут же выбросят.
    /// </summary>
    public void StopSpeaking()
    {
        if (_speaking is null && !TryClearTargetBuffer()) return;

        _speaking?.Cancel();

        SetStatus(Loc.Get("Sm.Tts.Status.Stopped"), true);
        RefreshStatus();
    }

    /// <summary>Выбросить недоигранное из целевого стрипа, если он вообще есть.</summary>
    private bool TryClearTargetBuffer()
    {
        if (!TryResolveTarget(out string targetId, out _)) return false;
        return _engine.ClearBufferedAudio(targetId);
    }

    /// <summary>Опустошить очередь озвучки.</summary>
    public void ClearQueue()
    {
        lock (_sync) _queue.Clear();
        RefreshStatus();
    }

    /// <summary>
    /// Принять реплику: разобрать, применить и поставить в очередь.
    ///
    /// Единственная точка входа для любого источника. Подключение к Twitch будет
    /// вызывать ровно её — вся остальная логика уже написана и покрыта
    /// проверками.
    ///
    /// Возвращается разобранная команда, а не «да/нет»: вызывающему нужно знать
    /// <em>почему</em> реплика не озвучена, иначе пользователь видел бы надпись
    /// «модуль работает» вместо «голоса такого нет».
    /// </summary>
    public TtsChatCommand Submit(string? user, string? text)
    {
        if (!IsEnabled)
        {
            SetStatus(Loc.Get("Sm.Tts.Status.Off"), false);
            return TtsChatCommand.Rejected(TtsChatCommandParser.NormalizeUser(user), TtsChatReject.ModuleOff);
        }

        if (!_speech.IsAvailable)
        {
            SetStatus(_speech.UnavailableReason, false);
            return TtsChatCommand.Rejected(TtsChatCommandParser.NormalizeUser(user), TtsChatReject.EngineUnavailable);
        }

        var settings = Settings;
        var command = TtsChatCommandParser.Parse(user, text, settings, IsKnownVoice);
        string login = command.User;

        switch (command.Reason)
        {
            case TtsChatReject.NotACommand:
                return command;

            case TtsChatReject.IgnoredUser:
                _logger.LogDebug("TTS: {User} в списке игнора", login);
                return command;

            case TtsChatReject.EmptyText:
                SetStatus(Loc.Get("Sm.Tts.Status.EmptyText", login), false);
                return command;

            case TtsChatReject.VoiceChangeDisabled:
                SetStatus(Loc.Get("Sm.Tts.Status.VoiceChangeOff"), false);
                return command;

            case TtsChatReject.UnknownVoice:
                SetStatus(Loc.Get("Sm.Tts.Status.UnknownVoice", command.Text), false);
                return command;
        }

        if (command.Action == TtsChatAction.SetVoice)
        {
            AssignVoice(login, command.Voice!);
            return command;
        }

        if (!AllowedByRate(login))
        {
            SetStatus(Loc.Get("Sm.Tts.Status.RateLimited", login, settings.MaxMessagesPerMinute), false);
            return TtsChatCommand.Rejected(login, TtsChatReject.RateLimited);
        }

        if (command.Truncated)
            _logger.LogWarning("TTS: реплика {User} обрезана до {Limit} символов",
                login, settings.MaxMessageChars);

        Enqueue(new TtsUtterance(login, command.Text));
        return command;
    }

    /// <summary>
    /// Принять реплику, сообщив только «озвучено или нет». Путь источника событий:
    /// там результат разбора никому не нужен, а команда из чата разбирается целиком.
    /// </summary>
    public bool SubmitMessage(string? user, string? text) => Submit(user, text).IsAccepted;

    /// <summary>
    /// Запомнить голос пользователя. Голос сохраняется сразу — иначе он был бы
    /// потерян при выходе из приложения, а это ровно то, ради чего команда и
    /// заводилась.
    /// </summary>
    private void AssignVoice(string user, string voice)
    {
        var settings = Settings;
        settings.UserVoices.RemoveAll(pair =>
            string.Equals(pair.User, user, StringComparison.OrdinalIgnoreCase));
        settings.UserVoices.Add(new TtsUserVoice(user, voice));
        _settings.Save();

        _logger.LogInformation("TTS: голос «{Voice}» закреплён за {User}", voice, user);
        SetStatus(Loc.Get("Sm.Tts.Status.VoiceAssigned", user, voice), true);
    }

    /// <summary>
    /// Дроссель: не больше <c>MaxMessagesPerMinute</c> реплик от одного
    /// пользователя за минуту. Окно скользящее, а не «сброс по таймеру»: иначе
    /// на границе минуты пользователь проскочил бы вдвое больше нормы.
    /// </summary>
    private bool AllowedByRate(string user)
    {
        var settings = Settings;
        int limit = Math.Max(1, settings.MaxMessagesPerMinute);
        var horizon = DateTime.UtcNow.AddMinutes(-1);

        lock (_sync)
        {
            if (!_recentPerUser.TryGetValue(user, out var stamps))
            {
                stamps = new Queue<DateTime>();
                _recentPerUser[user] = stamps;
            }

            while (stamps.Count > 0 && stamps.Peek() < horizon) stamps.Dequeue();
            if (stamps.Count >= limit) return false;

            stamps.Enqueue(DateTime.UtcNow);

            // Подчистка: иначе словарь рос бы вечно, по записи на каждого
            // когда-то написавшего в чат.
            if (_recentPerUser.Count > 512)
            {
                foreach (string key in _recentPerUser.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
                    _recentPerUser.Remove(key);
            }

            return true;
        }
    }

    /// <summary>
    /// Сколько секунд ещё должно идти из буфера, прежде чем считать фразу
    /// дослушанной.
    ///
    /// Не ноль, а 20 мс: буфер тает дискретно, квантом по 10 мс, и при нулевом
    /// пороге ожидание зависало бы на последнем кванте или проскакивало мимо него
    /// при совпадении. 20 мс — это 2 кванта, то есть запас на джиттер пробуждения.
    /// </summary>
    private const double HeardThresholdSeconds = 0.02;

    /// <summary>Период опроса буфера источника, мс.</summary>
    private const int PollMilliseconds = 20;

    private void Enqueue(TtsUtterance utterance)
    {
        // Пробуждение опирается на поток озвучки: без него сообщение легло бы в
        // очередь навсегда, и это выглядело бы как «модуль включён, но молчит».
        // Поэтому перед постановкой в очередь поток подтверждается, а не
        // предполагается: так снимается сценарий «синтез появился позже» и
        // «поток завершился с ошибкой» — в обоих случаях он поднимается здесь,
        // и оба попадают в журнал.
        EnsureWorkerRunning();

        int dropped = 0;
        lock (_sync)
        {
            _queue.Enqueue(utterance);

            int limit = Math.Max(1, Settings.MaxQueueLength);
            while (_queue.Count > limit)
            {
                _queue.Dequeue();
                dropped++;
            }

            // Собственно триггер: поток спит в Monitor.Wait, и отмена токена его
            // не будит — только этот пульс.
            Monitor.PulseAll(_sync);
        }

        // Молча выбрасывать начало очереди нельзя: через минуту пользователь
        // услышал бы не то, что писал в чат, и решил бы, что модуль «съедает
        // сообщения».
        if (dropped > 0)
            _logger.LogWarning("TTS: очередь переполнена (предел {Limit}), отброшено {Dropped}",
                Settings.MaxQueueLength, dropped);

        RefreshStatus();
    }

    /// <summary>Берёт очередную реплику, блокируясь до её появления или остановки.</summary>
    private TtsUtterance? TakeNext(CancellationToken token)
    {
        lock (_sync)
        {
            while (_queue.Count == 0 && !token.IsCancellationRequested)
                Monitor.Wait(_sync);

            return _queue.Count == 0 ? null : _queue.Dequeue();
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var next = TakeNext(token);
            if (next is null) continue;

            string? waitingOn = Speak(next, token);

            // Фраза посчитана озвученной, когда её ДОСЛУШАЛИ, а не когда синтезатор
            // вернулся: SAPI отдаёт весь текст в десятки раз быстрее реального
            // времени, так что без этого ожидания вторая фраза начала бы говорить
            // поверх первой, а «стоп» и очередь перестали бы означать что-либо.
            if (waitingOn is not null) WaitUntilHeard(waitingOn, token);
        }
    }

    /// <summary>
    /// Дожидается, пока стрип доиграет всё, что в него положили.
    ///
    /// Опрос, а не событие, сознательно: буфер тает со скоростью реального времени,
    /// то есть проверять раз в 20 мс — это 50 раз в секунду на потоке, который в
    /// это время всё равно ничего не делает. Событие от источника потребовало бы
    /// ещё одного контракта ради экономии, которая тут ничего не решает.
    /// </summary>
    private void WaitUntilHeard(string inputId, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _engine.GetBufferedSeconds(inputId) > HeardThresholdSeconds)
        {
            if (_speaking is { IsCancellationRequested: true }) return;
            Thread.Sleep(PollMilliseconds);
        }
    }

    /// <summary>
    /// Озвучивает одну реплику. Возвращает Id стрипа, за которым ещё надо
    /// подождать, — то есть не null, если в микшер ушло что-то, что ещё не прозвучало.
    /// </summary>
    private string? Speak(TtsUtterance utterance, CancellationToken token)
    {
        var settings = Settings;

        if (!TryResolveTarget(out string targetId, out string targetTitle))
        {
            SetStatus(Loc.Get("Sm.Tts.Status.NoTarget"), false);
            return null;
        }

        var voice = ResolveVoiceFor(utterance.User);
        if (voice is null)
        {
            SetStatus(Loc.Get("Sm.Tts.Status.NoVoices"), false);
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultVoice) && voice.IsRussian)
        {
            // Молча выбирать русский голос правильно только по названию нельзя:
            // пользователь должен видеть в журнале, на каком голосе заговорили.
            _logger.LogInformation("TTS: выбран русский голос по умолчанию «{Voice}»", voice.Name);
        }

        var options = new TtsSpeechOptions(voice.Name, settings.Rate, settings.Volume);

        using var speaking = CancellationTokenSource.CreateLinkedTokenSource(token);
        _speaking = speaking;

        SetStatus(Loc.Get("Sm.Tts.Status.Speaking", utterance.User, voice.Name), true);
        _logger.LogInformation("TTS: {User} → «{Voice}»: {Text}", utterance.User, voice.Name, utterance.Text);

        bool delivered = false;
        int lost = 0;

        try
        {
            _speech.Speak(utterance.Text, options, chunk =>
            {
                if (_engine.PushAudio(targetId, chunk))
                {
                    delivered = true;
                    return;
                }

                lost += chunk.Length;
            }, speaking.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TTS: фраза {User} прервана", utterance.User);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS: синтез реплики {User} не удался: {Message}",
                utterance.User, ex.Message);
            SetStatus(Loc.Get("Sm.Tts.Status.SynthesisFailed", ex.Message), false);
        }
        finally
        {
            if (ReferenceEquals(_speaking, speaking)) _speaking = null;
        }

        if (!delivered && lost > 0)
        {
            // Начало фразы ушло, пока микшер не был готов; так бывает при снятии
            // маршрута посреди реплики. Молча пропавший звук хуже записи.
            _logger.LogWarning(
                "TTS: реплика {User} не дошла до микшера (потеряно {Samples} сэмплов) — " +
                "проверьте, что канал «{Strip}» включён в маршрут и движок запущен",
                utterance.User, lost / 2, targetTitle);
            SetStatus(Loc.Get("Sm.Tts.Status.NotDelivered", targetTitle), false);
            return null;
        }

        RefreshStatus();

        // Ничего не ушло — ждать нечего, иначе следующая фраза встала бы на паузу
        // до конца несуществующего звука.
        return delivered ? targetId : null;
    }

    /// <summary>
    /// Находит входной стрип, в который уходит голос.
    ///
    /// Сначала по Id (он переживает перезапуск приложения), затем по DeviceId —
    /// на случай, если стрип был пересоздан. Сгенерированный стрип (SM-E01) ни
    /// одного из них не имеет DeviceId, но его Id устойчив, поэтому первый путь
    /// для него и есть верный.
    /// </summary>
    private bool TryResolveTarget(out string inputId, out string title)
    {
        inputId = "";
        title = "";

        var settings = Settings;
        if (string.IsNullOrWhiteSpace(settings.TargetInputId)) return false;

        var strip = _engine.Inputs.FirstOrDefault(i => i.Id == settings.TargetInputId)
                    ?? _engine.Inputs.FirstOrDefault(i =>
                        !string.IsNullOrEmpty(settings.TargetInputDeviceId) &&
                        string.Equals(i.DeviceId, settings.TargetInputDeviceId, StringComparison.OrdinalIgnoreCase));

        if (strip is null) return false;

        inputId = strip.Id;
        title = string.IsNullOrWhiteSpace(strip.ChannelName) ? strip.Name : strip.ChannelName;
        return true;
    }

    /// <summary>Есть ли куда озвучивать: микшер запущен и стрип куда-то послан.</summary>
    public bool CanSpeakNow() => TryResolveTarget(out _, out _) && _engine.IsRunning;

    /// <summary>Целевой канал для окна настроек (пусто — не выбран или исчез).</summary>
    public string TargetTitle => TryResolveTarget(out _, out string title) ? title : "";

    private void OnMessage(TtsChatMessage message) => SubmitMessage(message.User, message.Text);

    private void SetStatus(string text, bool ok)
    {
        if (_status == text && _statusOk == ok) return;

        _status = text;
        _statusOk = ok;
        RaiseStatusChanged();
    }

    private void RefreshStatus()
    {
        if (!IsEnabled)
        {
            SetStatus(Loc.Get("Sm.Tts.Status.Off"), false);
            return;
        }

        if (!_speech.IsAvailable)
        {
            SetStatus(_speech.UnavailableReason, false);
            return;
        }

        int queued = QueueLength;
        SetStatus(queued > 0
            ? Loc.Get("Sm.Tts.Status.Queued", queued)
            : Loc.Get("Sm.Tts.Status.Running"), true);
    }

    private void RaiseStatusChanged()
    {
        // Статус меняется из потока озвучки и из потока источника, а читает его
        // окно настроек — маршрутизируем в UI-поток.
        try
        {
            _ = _dispatcher.InvokeAsync(() => StatusChanged?.Invoke());
        }
        catch (Exception)
        {
            // Приложение закрывается — подписчику это уже не нужно.
        }
    }

    /// <summary>Реплика, ждущая очереди. Голос определяется в момент озвучки.</summary>
    private sealed record TtsUtterance(string User, string Text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.MessageReceived -= OnMessage;

        if (IsEnabled) StopEverything(Loc.Get("Sm.Tts.Status.Off"));

        _source.Dispose();
    }
}
