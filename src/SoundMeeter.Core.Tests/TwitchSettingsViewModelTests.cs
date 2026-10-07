using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Twitch;
using SoundMeeter.ViewModels;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Окно подключения к Twitch (SM-F01): вход, канал, статус трансляции.
///
/// Проверяется то, что ломается тихо. Главное здесь — вход: код должен остаться на
/// экране, пока пользователь ушёл в браузер, и исчезнуть только когда вход состоялся
/// или был отозван. Убрать код раньше — значит вернуть человека к пустому окну и
/// заставить начинать заново.
///
/// Отдель��о проверяется «состояние неизвестно» вместо «не в эфире»: обрыв сети на
/// эфире, показанный как завершившаяся трансляция, хуже любой ошибки.
///
/// Сеть не трогается: подменяются хост и состояние интеграции. Опрос кода
/// разбужается заданием ответа, а не ожиданием по таймеру.
/// </summary>
public class TwitchSettingsViewModelTests
{
    [Fact]
    public void AnUnconfiguredBuildSaysSoInsteadOfFailingSilently()
    {
        using var vm = Create(out _, configured: false);

        // Пока client ID не вписан в код, вход невозможен. Пользователь должен увидеть
        // это прямо, а не нажать «войти» и получить тишину.
        Assert.False(vm.IsConfigured);
        Assert.False(vm.CanStartLogin);
        Assert.Equal(Loc.Get("Sm.Twitch.NotConfigured"), vm.Status);
    }

    [Fact]
    public async Task SigningInShowsTheCodeAndKeepsItVisible()
    {
        using var vm = Create(out var host, configured: true);

        host.Challenge = Challenge();
        await vm.StartLoginAsync();

        // Код остаётся на экране: пользователь уходит в браузер вводить его и вернётся
        // к этому же окну. Убрать код сразу значило бы заставить начинать заново.
        Assert.Equal("CODE", vm.LoginCode);
        Assert.True(vm.HasLoginCode);
        Assert.Contains("twitch.tv/activate", vm.ActivationUrl);
    }

    [Fact]
    public async Task TheCodeIsClearedOnceTheLoginSucceeds()
    {
        using var vm = Create(out var host, configured: true);

        host.Challenge = Challenge();
        await vm.StartLoginAsync();
        host.PollResult = new DeviceAuthResult(DeviceAuthStatus.Authorized, null);
        await vm.PollOnceAsync();

        WaitFor(() => vm.LoginCode.Length == 0, "код остался на экране после входа");

        Assert.Equal("", vm.ActivationUrl);
        Assert.True(vm.IsAuthorized);
        Assert.Equal("reader", vm.AuthorizedLogin);
    }

    [Fact]
public async Task ARejectedLoginClearsTheCodeAndSaysWhy()
    {
        // Отказ приходит, когда входа ещё не было: вход и есть то, что отклонили.
        using var vm = Create(out var host, configured: true, authorized: false);

        host.Challenge = Challenge();
        await vm.StartLoginAsync();
        host.RejectLogin(DeviceAuthStatus.Denied);
        await vm.PollOnceAsync();

        WaitFor(() => vm.LoginCode.Length == 0, "код остался после отказа");

        Assert.False(vm.IsAuthorized);
        Assert.Equal(Loc.Get("Sm.Twitch.LoginDenied"), vm.Feedback);
        Assert.False(vm.IsStatusOk);
    }

    [Fact]
public async Task AnExpiredCodeIsReportedAsExpiredNotAsAFailure()
    {
        using var vm = Create(out var host, configured: true, authorized: false);

        host.Challenge = Challenge();
        await vm.StartLoginAsync();
        host.RejectLogin(DeviceAuthStatus.Expired);
        await vm.PollOnceAsync();

        WaitFor(() => vm.LoginCode.Length == 0, "код остался после истечения");

        // «Код истёк» и «что-то сломалось» — разные подсказки для пользователя: первое
        // лечится новым кодом, второе перезапуском.
        Assert.Equal(Loc.Get("Sm.Twitch.LoginExpired"), vm.Feedback);
    }

    [Fact]
    public async Task WaitingForTheUserDoesNotShowAnError()
    {
        using var vm = Create(out var host, configured: true);

        host.Challenge = Challenge();
        await vm.StartLoginAsync();
        host.RejectLogin(DeviceAuthStatus.Pending);

        // «Пользователь ещё в браузере» — нормальное состояние, а не ошибка. Показывать
        // её красным значило бы тревожить на каждом опросе.
        Assert.True(vm.IsStatusOk);
        Assert.Equal("CODE", vm.LoginCode);
    }

    [Fact]
    public async Task AFailedLoginRequestSaysSoAndShowsNoCode()
    {
        using var vm = Create(out var host, configured: true);

        host.Challenge = null;   // сервер отказал
        await vm.StartLoginAsync();

        Assert.Equal("", vm.LoginCode);
        Assert.Equal(Loc.Get("Sm.Twitch.LoginFailed"), vm.Feedback);
        Assert.False(vm.IsStatusOk);
    }

    [Fact]
    public void TheChannelAndFlagsAreAppliedToTheLiveSettings()
    {
        using var vm = Create(out var host, configured: true);

        vm.Enabled = true;
        vm.ChannelLogin = "streamer";
        vm.ShowStreamStatus = false;
        vm.ApplyCommand.Execute(null);

        // Применение обязано дойти до настроек: без него включение чата снялось бы при
        // первом же фоновом сохранении, и модуль выглядел бы сломанным.
        var saved = host.LastApplied;
        Assert.NotNull(saved);
        Assert.True(saved!.Enabled);
        Assert.Equal("streamer", saved.ChannelLogin);
        Assert.False(saved.ShowStreamStatus);
    }

    [Fact]
    public void ApplyingWithASignedOutAccountSaysThatSigningInIsStillNeeded()
    {
        using var vm = Create(out var host, configured: true, authorized: false);

        vm.Enabled = true;
        vm.ApplyCommand.Execute(null);

        // Подсказка «войдите» вместо простого «сохранено»: иначе пользователь нажмёт
        // «применить», увидит, что ничего не происходит, и не поймёт, чего не хватает.
        Assert.Equal(Loc.Get("Sm.Twitch.SavedNeedLogin"), vm.Feedback);
    }

    [Fact]
    public void AnUnknownStreamIsNotReportedAsOffline()
    {
        using var vm = Create(out _, configured: true, stream: TwitchStreamState.Unknown);

        // Проверка, ради которой состояния разведены: обрыв сети не должен
        // показываться как завершившаяся трансляция.
        Assert.Equal(Loc.Get("Sm.Twitch.Stream.Unknown"), vm.StreamStatusText);
    }

    [Fact]
    public void ALiveStreamIsReportedAsLive()
    {
        using var vm = Create(out _, configured: true, stream: TwitchStreamState.Live);

        Assert.Equal(Loc.Get("Sm.Twitch.Stream.Live"), vm.StreamStatusText);
    }

    [Fact]
    public void TheStatusLineSaysWhenSignedOut()
    {
        using var vm = Create(out _, configured: true, authorized: false);

        Assert.Equal(Loc.Get("Sm.Twitch.Status.NotAuthorized"), vm.Status);
    }

    [Fact]
    public void TheStatusLineMentionsTheConnectionAndTheStream()
    {
        using var vm = Create(out _, configured: true,
            authorized: true, chatConnected: true, stream: TwitchStreamState.Live);

        string status = vm.Status;

        Assert.Contains(Loc.Get("Sm.Twitch.Status.ChatConnected"), status, StringComparison.Ordinal);
        Assert.Contains(Loc.Get("Sm.Twitch.Stream.Live"), status, StringComparison.Ordinal);
    }

    [Fact]
public async Task SigningOutClearsTheAccount()
    {
        using var vm = Create(out var host, configured: true, authorized: true);

await vm.StartLogoutAsync();

        // После выхода не остаётся признака входа: окно не должно показывать «вошли» при
        // отсутствии токена.
        Assert.True(host.RequestedLogouts > 0);
        Assert.False(vm.IsAuthorized);
        Assert.Equal(Loc.Get("Sm.Twitch.LoggedOut"), vm.Feedback);
    }

    [Fact]
    public async Task ClosingTheWindowStopsTheCodePolling()
    {
        var vm = Create(out var host, configured: true);
        host.Challenge = Challenge();
        await vm.StartLoginAsync();

        vm.Dispose();

        // Опрос кода ходит в сеть по таймеру. Оставленный после закрытия окна, он
        // продолжал бы спрашивать сервер и держать токен без всякой видимой причины.
        Assert.False(vm.IsPollingLogin);
    }

    /// <summary>
    /// Ожидание с пределом. Без него проверка ждала бы вечно и падала бы по таймауту
    /// всей сборки вместо внятного сообщения о том, что не наступило.
    /// </summary>
    private static void WaitFor(Func<bool> reached, string because)
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                        + (long)(10.0 * System.Diagnostics.Stopwatch.Frequency);

        while (!reached())
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                Assert.Fail(because);

            Thread.Sleep(10);
        }
    }

    private static DeviceCodeChallenge Challenge() =>
        new("DC", "CODE", "https://www.twitch.tv/activate?public=true&device-code=CODE", 1800, 5);

    private static TwitchSettingsViewModel Create(
        out TwitchHostStub host,
        bool configured = true,
        bool authorized = true,
        bool chatConnected = false,
        TwitchStreamState stream = TwitchStreamState.Unknown)
    {
        host = new TwitchHostStub
        {
            Configured = configured,
            State =
            {
                IsAuthorized = authorized,
                IsChatConnected = chatConnected,
                StreamState = stream,
            },
        };

        // Интервал опроса намеренно длинный: таймер окна не должен ничего делать сам,
        // иначе проверка зависела бы от его тактов. Ответ подставляется через
        // PollOnceAsync.
        var vm = new TwitchSettingsViewModel(host, host.State) { PollInterval = TimeSpan.FromHours(1) };
        return vm;
    }

    /// <summary>
    /// Хост с управляемыми ответами сервера: без этого проверка ждала бы настоящих
    /// секунд опроса и зависела бы от сети.
    /// </summary>
    private sealed class TwitchHostStub : ITwitchHost
    {
        public bool IsTwitchConfigured => Configured;

        public bool Configured { get; init; } = true;

        /// <summary>Состояние интеграции, которое окно читает.</summary>
        public TwitchStateStub State { get; init; } = new();

        /// <summary>Что вернёт запрос кода. null — сервер отказал.</summary>
        public DeviceCodeChallenge? Challenge { get; set; }

        /// <summary>Что вернёт опрос кода.</summary>
        public DeviceAuthResult PollResult { get; set; } = new(DeviceAuthStatus.Pending, null);

        /// <summary>Последние применённые настройки.</summary>
        public TwitchSettings? LastApplied { get; private set; }

        public int RequestedLogouts { get; private set; }

        public List<string> OpenedUrls { get; } = new();

        public Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Challenge);

        public Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            // Пользователь подтвердил вход: состояние переходит в «вошли».
            if (PollResult.Status == DeviceAuthStatus.Authorized)
            {
                State.IsAuthorized = true;
                State.AuthorizedLogin = "reader";
            }

            return Task.FromResult(PollResult);
        }

        public Task LogoutTwitchAsync(CancellationToken cancellationToken = default)
        {
            RequestedLogouts++;
            State.IsAuthorized = false;
            return Task.CompletedTask;
        }

        public void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true) => LastApplied = updated;

        public void OpenTwitchConsole() => OpenedUrls.Add("console");

        public void OpenTwitchActivation(string verificationUri) => OpenedUrls.Add(verificationUri);

        /// <summary>Сервер ответил отказом или «пока ждём».</summary>
        public void RejectLogin(DeviceAuthStatus status) =>
            PollResult = new DeviceAuthResult(status, null);

        public Task SignOut() => LogoutTwitchAsync();
    }

    /// <summary>
    /// Состояние интеграции, задаваемое проверкой: без сети и фоновых потоков.
    ///
    /// Отдельная реализация <see cref="ITwitchState"/>, а не наследник настоящего
    /// сервиса: у сервиса есть поток чтения чата и поток опроса трансляции, и поднимать
    /// их ради проверки окна значило бы проверять не окно.
    /// </summary>
    private sealed class TwitchStateStub : ITwitchState
    {
        public bool IsAuthorized { get; set; } = true;
        public string AuthorizedLogin { get; set; } = "reader";
        public bool IsChatConnected { get; set; }
        public TwitchStreamState StreamState { get; set; } = TwitchStreamState.Unknown;
        public TwitchSettings Settings { get; set; } = new();

#pragma warning disable CS0067   // событие никто не поднимает: сокета тут нет
        public event Action? StatusChanged;
#pragma warning restore CS0067
    }
}