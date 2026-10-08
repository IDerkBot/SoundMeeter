using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Twitch;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Окно подключения к Twitch (SM-F01): вход, канал для чтения чата и показ статуса
/// трансляции.
///
/// Отдельное окно, а не секция в настройках синтеза речи, по одной причине: вход в
/// Twitch нужен и сам по себе, без озвучки. Индикатор трансляции и чат — не свойства
/// синтезатора, а отдельная интеграция; смешанные, они тянули бы друг за собой и
/// путали бы вопрос «модуль озвучки включён» с «к аккаунту подключено».
///
/// Вход двухшаговый, и это не усложнение ради усложнения: Twitch отдаёт код, который
/// пользователь вводит на своей странице, а приложение тем временем опрашивает сервер
/// (см. <see cref="TwitchAuthService"/>). Пользователь видит код и адрес страницы,
/// ничего не держит в буфере обмена и не переносит руками.
/// </summary>
public sealed partial class TwitchSettingsViewModel : LocalizedViewModel
{
    /// <summary>
    /// Как часто опрашивать сервер, пока пользователь вводит код.
    ///
    /// Сервер задаёт интервал сам, и это значение используется как основа: проверка
    /// ждёт по-настоящему и подставляет ответ, а не ждёт настоящих пяти секунд.
    /// </summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Опрашивать сервер сейчас, не дожидаясь расписания. Для проверок: они задают
    /// ответ и хотят увидеть результат без ожидания по таймеру.
    /// </summary>
    internal async Task PollOnceAsync()
    {
        if (_challenge is not { } challenge) return;

        var result = await _host.ContinueTwitchLoginAsync(challenge).ConfigureAwait(false);
        ApplyPollResult(result);
    }

    private readonly ITwitchHost _host;
    private readonly ITwitchState _twitch;

    /// <summary>Код текущего входа. null — вход не начат.</summary>
    private DeviceCodeChallenge? _challenge;

    /// <summary>
    /// Идёт ли фоновый опрос кода. Для проверок: опрос ходит в сеть по таймеру, и
    /// убедиться, что он остановлен, можно только по этому признаку — сам таймер
    /// снаружи не виден.
    /// </summary>
    internal bool IsPollingLogin => _polling is not null;

    /// <summary>Отмена опроса кода. null — опрос не идёт.</summary>
    private CancellationTokenSource? _polling;

    public TwitchSettingsViewModel(ITwitchHost host, ITwitchState twitch)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(twitch);

        _host = host;
        _twitch = twitch;

        var settings = TwitchSettingsFromHost();
        Enabled = settings.Enabled;
        ChannelLogin = settings.ChannelLogin;
        ShowStreamStatus = settings.ShowStreamStatus;

        // Строка состояния заполняется сразу: окно обязано объяснять, почему вход
        // невозможен или не выполнен, ещё до первого нажатия кнопки. Иначе
        // невыясненная пустота выглядела бы поломкой.
        RefreshStatus();

        _twitch.StatusChanged += RefreshStatus;
    }

    /// <summary>Настройки интеграции из живого снимка настроек.</summary>
    private TwitchSettings TwitchSettingsFromHost() => _twitch.Settings;

    #region Поля

    /// <summary>Читать чат Twitch.</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Канал, чей чат читается. Без «@».</summary>
    [ObservableProperty]
    private string _channelLogin = "";

    /// <summary>Показывать, идёт ли трансляция.</summary>
    [ObservableProperty]
    private bool _showStreamStatus = true;

    /// <summary>
    /// Код, который пользователь вводит на странице Twitch. Пусто — вход не идёт.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginCode))]
    private string _loginCode = "";

    /// <summary>Страница, где вводится код.</summary>
    [ObservableProperty]
    private string _activationUrl = "";

    /// <summary>Результат последнего действия (зелёным/красным — по IsStatusOk).</summary>
    [ObservableProperty]
    private string _feedback = "";

    [ObservableProperty]
    private bool _isStatusOk;

    /// <summary>Строка состояния подключения.</summary>
    [ObservableProperty]
    private string _status = "";

    #endregion

    #region Производные

    /// <summary>Вход выполнен.</summary>
    public bool IsAuthorized => _twitch.IsAuthorized;

    /// <summary>Логин, под которым выполнен вход.</summary>
    public string AuthorizedLogin => _twitch.AuthorizedLogin;

    /// <summary>Чат подключён.</summary>
    public bool IsChatConnected => _twitch.IsChatConnected;

    /// <summary>Приложение собрано: в коде есть client ID.</summary>
    public bool IsConfigured => _host.IsTwitchConfigured;

    /// <summary>Идёт ли вход: код показан, ждём подтверждения.</summary>
    public bool HasLoginCode => LoginCode.Length > 0;

    /// <summary>Можно ли начинать вход.</summary>
    public bool CanStartLogin => IsConfigured && !IsAuthorized;

    /// <summary>Можно ли выйти. Выйти из аккаунта, в который не входили, нечего.</summary>
    public bool CanLogout => IsAuthorized;

    /// <summary>Что показывать в поле канала: пусто — ещё не задан.</summary>
    public string ChannelHint => ChannelLogin.Trim().Length == 0
        ? Loc.Get("Sm.Twitch.ChannelHint")
        : Loc.Get("Sm.Twitch.ChannelSet", _twitch.StreamState switch
        {
            TwitchStreamState.Live => Loc.Get("Sm.Twitch.Stream.Live"),
            TwitchStreamState.Offline => Loc.Get("Sm.Twitch.Stream.Offline"),
            _ => Loc.Get("Sm.Twitch.Stream.Unknown"),
        });

    /// <summary>
    /// Текст состояния одной строкой.
    ///
    /// Собирается здесь, а не берётся готовой строкой из сервиса: подписи зависят от
    /// языка интерфейса, а сервис о языке не знает.
    /// </summary>
    public string StatusText => BuildStatus();

    /// <summary>Текст состояния трансляции для главного окна.</summary>
    public string StreamStatusText => _twitch.StreamState switch
    {
        TwitchStreamState.Live => Loc.Get("Sm.Twitch.Stream.Live"),
        TwitchStreamState.Offline => Loc.Get("Sm.Twitch.Stream.Offline"),
        _ => Loc.Get("Sm.Twitch.Stream.Unknown"),
    };

    #endregion

    #region Команды

    /// <summary>
    /// Начать вход: запросить код и начать опрашивать сервер.
    ///
    /// Код остаётся на экране, пока пользователь не подтвердит вход или не откажется:
    /// автоматически убирать его нельзя — иначе человек, ушедший заполнить код в
    /// браузере, вернулся бы и не понял, что надо вводить заново.
    /// </summary>
    [RelayCommand]
    private Task LoginAsync() => StartLoginAsync();

    /// <summary>
    /// Начать вход. Отдельный метод, а не только команда, потому что проверка
    /// вызывает его напрямую: иначе ей пришлось бы звать команду и ждать её
    /// завершения, не зная, когда оно наступит.
    /// </summary>
    internal async Task StartLoginAsync()
    {
        if (!IsConfigured)
        {
            SetFeedback(Loc.Get("Sm.Twitch.NotConfigured"), false);
            return;
        }

        SetFeedback(Loc.Get("Sm.Twitch.LoginStarting"), true);

        var challenge = await _host.BeginTwitchLoginAsync();
        if (challenge is null)
        {
            SetFeedback(Loc.Get("Sm.Twitch.LoginFailed"), false);
            return;
        }

        _challenge = challenge;
        LoginCode = challenge.UserCode;
        ActivationUrl = challenge.VerificationUri;
        SetFeedback(Loc.Get("Sm.Twitch.LoginCodeShown", challenge.UserCode), true);

        StartPolling(challenge);
    }

    /// <summary>
    /// Проверять подтверждение, пока код жив.
    ///
    /// Опрос с пределом по времени жизни кода: оставленный в фоне код иначе
    /// спрашивал бы сервер до бесконечности.
    /// </summary>
    private void StartPolling(DeviceCodeChallenge challenge)
    {
        StopPolling();

        _polling = new CancellationTokenSource();
        var token = _polling.Token;
        var deadline = DateTimeOffset.UtcNow + challenge.Lifetime;

        _ = PollAsync(challenge, deadline, token);
    }

    private async Task PollAsync(DeviceCodeChallenge challenge, DateTimeOffset deadline,
        CancellationToken token)
    {
        // Сервер задаёт интервал сам, и спрашивать чаще он не разрешает: Twitch ответит
        // ошибкой. Указанный интервал берётся с запасом, чтобы не попасть в её край.
        var interval = challenge.PollInterval + TimeSpan.FromMilliseconds(500);
        if (PollInterval < interval) interval = PollInterval;

        try
        {
            while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(interval, token);

                var result = await _host.ContinueTwitchLoginAsync(challenge);
                if (!ApplyPollResult(result)) return;
            }

            if (!token.IsCancellationRequested) FailLogin(Loc.Get("Sm.Twitch.LoginExpired"));
        }
        catch (OperationCanceledException)
        {
            // Отмена — это выход из окна или выключение модуля, а не ошибка.
        }
    }

    /// <summary>
    /// Разобрать ответ опроса. false — опрос окончен (вход состоялся, отказ или
    /// истечение), true — ждать дальше.
    ///
    /// Решение о завершении вынесено отдельно, потому что им пользуются и таймер
    /// окна, и проверка: иначе разбор пришлось бы дублировать, а расхождение двух
    /// копий означало бы, что окно и его проверка ведут себя по-разному.
    /// </summary>
    private bool ApplyPollResult(DeviceAuthResult result)
    {
        switch (result.Status)
        {
            case DeviceAuthStatus.Authorized:
                FinishLogin(result.Token?.Login ?? "");
                return false;

            case DeviceAuthStatus.Pending:
                return true;

            case DeviceAuthStatus.Denied:
                FailLogin(Loc.Get("Sm.Twitch.LoginDenied"));
                return false;

            case DeviceAuthStatus.Expired:
                FailLogin(Loc.Get("Sm.Twitch.LoginExpired"));
                return false;

            default:
                // «Что-то пошло не так» при ожидании — это обычно сеть моргнула. Ошибку
                // показывать рано: пользователь может ещё в браузере, и через минуту всё
                // сложилось бы само.
                return true;
        }
    }

    private void FinishLogin(string login)
    {
        StopPolling();
        LoginCode = "";
        ActivationUrl = "";

        SetFeedback(Loc.Get("Sm.Twitch.LoginDone", login), true);
        RefreshStatus();
    }

    private void FailLogin(string reason)
    {
        StopPolling();
        LoginCode = "";
        ActivationUrl = "";

        SetFeedback(reason, false);
        RefreshStatus();
    }

    private void StopPolling()
    {
        var polling = _polling;
        _polling = null;
        if (polling is null) return;

        polling.Cancel();
        polling.Dispose();
    }

    /// <summary>Открыть страницу, где вводится код.</summary>
    [RelayCommand]
    private void OpenActivationPage()
    {
        if (ActivationUrl.Length == 0) return;

        _host.OpenTwitchActivation(ActivationUrl);
    }

    /// <summary>Открыть консоль Twitch — там регистрируется приложение.</summary>
    [RelayCommand]
    private void OpenConsole() => _host.OpenTwitchConsole();

    /// <summary>Выйти из аккаунта.</summary>
    [RelayCommand]
    private Task LogoutAsync() => StartLogoutAsync();

    /// <summary>Выйти из аккаунта. Отдельный метод — по той же причине, что и вход.</summary>
    internal async Task StartLogoutAsync()
    {
        // Останавливается опрос кода: после выхода продолжать спрашивать сервер
        // не про что.
        StopPolling();

        await _host.LogoutTwitchAsync();

        SetFeedback(Loc.Get("Sm.Twitch.LoggedOut"), true);
        RefreshStatus();
    }

    /// <summary>
    /// Применить настройки.
    ///
    /// Канал применить можно и до входа: подписка создастся сама, как только
    /// пользователь войдёт. Иначе настройку пришлось бы повторять после входа, а
    /// это как раз тот случай, когда человек уже невнимателен.
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        _host.ApplyTwitchSettings(new TwitchSettings
        {
            Enabled = Enabled,
            ChannelLogin = ChannelLogin,
            ShowStreamStatus = ShowStreamStatus,
            AuthorizedLogin = AuthorizedLogin,
            ChannelUserId = TwitchSettingsFromHost().ChannelUserId,
        });

        RefreshStatus();

        SetFeedback(Enabled && !IsAuthorized
            ? Loc.Get("Sm.Twitch.SavedNeedLogin")
            : Loc.Get("Sm.Twitch.Saved"), true);
    }

    #endregion

    #region Служебное

    private string BuildStatus()
    {
        if (!IsConfigured) return Loc.Get("Sm.Twitch.NotConfigured");

        if (!IsAuthorized) return Loc.Get("Sm.Twitch.Status.NotAuthorized");

        string chat = IsChatConnected
            ? Loc.Get("Sm.Twitch.Status.ChatConnected")
            : Loc.Get("Sm.Twitch.Status.ChatDisconnected");

        return $"{chat}, {StreamStatusText}";
    }

    private void RefreshStatus()
    {
        Status = StatusText;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsAuthorized));
        OnPropertyChanged(nameof(AuthorizedLogin));
        OnPropertyChanged(nameof(IsChatConnected));
        OnPropertyChanged(nameof(CanStartLogin));
        OnPropertyChanged(nameof(CanLogout));
        OnPropertyChanged(nameof(ChannelHint));
        OnPropertyChanged(nameof(StreamStatusText));
    }

    private void SetFeedback(string text, bool ok)
    {
        Feedback = text;
        IsStatusOk = ok;
    }

    protected override void OnLanguageChangedCore() => RefreshStatus();

    protected override void DisposeCore()
    {
        // Опрос кода обязан быть остановлен при закрытии окна: он ходит в сеть по
        // таймеру, и оставленный в фоне, он продолжал бы спрашивать сервер после
        // закрытия окна и удерживал токен.
        StopPolling();
        _challenge = null;
        _twitch.StatusChanged -= RefreshStatus;
    }

    #endregion
}