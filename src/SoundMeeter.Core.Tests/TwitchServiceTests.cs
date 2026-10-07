using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.TextToSpeech;
using SoundMeeter.Services.Twitch;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Интеграция с Twitch (SM-F01) и её мост в модуль синтеза речи.
///
/// Проверяется не «соединение установилось», а то, что модуль получает реплики и
/// знает, почему их нет. Разбор команд, дроссель и синтез живут в модуле и здесь не
/// повторяются; если чат молчит, пользователь обязан видеть причину — «модуль не
/// работает» без причины хуже, чем конкретный отказ.
///
/// Сеть не трогается: подменяются вход, клиент API и читатель чата. Сам разбор
/// кадров проверяется отдельно и тоже без сети.
///
/// Отдельно проверяется, что смена канала обязательно переподключает подписку: она
/// адресована конкретному каналу, и без переподключения старая молча перестала бы
/// приносить сообщения — модуль выглядел бы включённым и не работал бы.
/// </summary>
public class TwitchServiceTests
{
    [Fact]
    public void WithoutALoginNothingIsConnectedAndNothingIsQueried()
    {
        using var fixture = new Fixture(authorized: false);

        fixture.Service.ApplySettings();

        // Без входа ходить в Twitch нельзя, и делать это «на всякий случай» означало
        // бы слать запросы без токена и получать 401 без внятной причины.
        Assert.False(fixture.Service.IsChatConnected);
        Assert.Empty(fixture.Api.Requests);
    }

    [Fact]
    public void AnEnabledChatWithoutAChannelDoesNotTryToConnect()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;

        fixture.Service.ApplySettings();

        // Канал не выбран — подключаться не к чему. Это не ошибка: пользователь задаст
        // канал позже, и подписка создастся сама.
        Assert.False(fixture.Service.IsChatConnected);
        Assert.False(fixture.Chat.Started);
    }

    [Fact]
    public void AnEnabledChatResolvesTheChannelIdentifierOnceAndCachesIt()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "@Streamer";
        fixture.Api.UserId = "777";

        fixture.Service.ApplySettings();
        fixture.Service.ApplySettings();

        // Кэш обязателен: без него каждое переподключение и каждый перезапуск
        // приложения делали бы лишний запрос за тем же значением.
        Assert.Equal("777", fixture.Settings.ChannelUserId);
        Assert.Equal(1, fixture.Api.UserIdQueries);
    }

    [Fact]
    public void AnUnknownChannelIsNotTreatedAsConnected()
    {
        var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "nobody";
        fixture.Api.UserId = null;

        fixture.Service.ApplySettings();

        // Подключиться к несуществующему каналу молча нельзя: пользователь увидел бы
        // «подключено» и ждал бы сообщения, которых не будет никогда.
        Assert.False(fixture.Service.IsChatConnected);
        Assert.Equal("", fixture.Settings.ChannelUserId);
        fixture.Dispose();
    }

    [Fact]
    public void ADisabledChatIsNotConnectedEvenWithALogin()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = false;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";

        fixture.Service.ApplySettings();

        // Галочка снята — пользователь не хочет, чтобы приложение читал его чат.
        // Это обязано уважаться буквально.
        Assert.False(fixture.Service.IsChatConnected);
    }

    [Fact]
    public void ChangingTheChannelRestartsTheSubscription()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "first";
        fixture.Api.UserId = "111";

        fixture.Service.ApplySettings();
        Assert.True(fixture.Chat.Started);

        fixture.Settings.ChannelLogin = "second";
        fixture.Settings.ChannelUserId = "";
        fixture.Api.UserId = "555";

        fixture.Service.ApplySettings();

        // Подписка адресована конкретному каналу, поэтому старая молча перестала бы
        // приносить сообщения. Переподключение — обязательное, а не аккуратность.
        Assert.True(fixture.Chat.Stopped);
        Assert.Equal("555", fixture.Settings.ChannelUserId);
    }

    [Fact]
    public void MessagesFromTheChatReachTheSpeechModule()
    {
        using var fixture = new Fixture(authorized: true);
        var received = new List<TtsChatMessage>();
        fixture.Service.MessageReceived += received.Add;

        fixture.Chat.Raise(new TwitchChatMessage("viewer", "!tts привет"));

        // Мост передаёт реплику ровно такой, какой её прислал чат. Разбор команд — дело
        // модуля: если разбирать здесь, логика разъехалась бы на две копии.
        Assert.Single(received);
        Assert.Equal("viewer", received[0].User);
        Assert.Equal("!tts привет", received[0].Text);
    }

    [Fact]
    public void AHandlerThatThrowsDoesNotStopTheChat()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Service.MessageReceived += _ => throw new InvalidOperationException("модуль упал");

        fixture.Chat.Raise(new TwitchChatMessage("viewer", "!tts раз"));
        fixture.Chat.Raise(new TwitchChatMessage("viewer", "!tts два"));

        // Один сбойный обработчик (а он один — модуль озвучки) не должен ронять чтение
        // чата: иначе он просто перестал бы работать без объяснений.
        Assert.Equal(2, fixture.Chat.Raised);
    }

    [Fact]
    public void TheStreamStatusIsNotQueriedUnlessItIsAskedFor()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";
        fixture.Settings.ShowStreamStatus = false;

        fixture.Service.ApplySettings();

        // Опрос — это запрос каждые полминуты. Кому он не нужен, тот не должен за
        // него платить.
        Assert.Equal(0, fixture.Api.StreamQueries);
    }

    [Fact]
    public void ALiveChannelIsReportedAsLive()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ShowStreamStatus = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";
        fixture.Api.Live = true;

        fixture.Service.ApplySettings();
        fixture.WaitForStatus(() => fixture.Service.StreamState == TwitchStreamState.Live);

        Assert.Equal(TwitchStreamState.Live, fixture.Service.StreamState);
    }

    [Fact]
    public void AnOfflineChannelIsReportedAsOffline()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ShowStreamStatus = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";
        fixture.Api.Live = false;

        fixture.Service.ApplySettings();
        fixture.WaitForStatus(() => fixture.Service.StreamState == TwitchStreamState.Offline);

        Assert.Equal(TwitchStreamState.Offline, fixture.Service.StreamState);
    }

    [Fact]
    public void AnUnreachableNetworkDoesNotLookLikeAFinishedStream()
    {
        using var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ShowStreamStatus = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";
        fixture.Api.Live = null;

        fixture.Service.ApplySettings();

        // Ради этого и различаются «неизвестно» и «офлайн»: обрыв сети на середине
        // стрима не должен показывать «не в эфире» пользователю, который как раз ведёт
        // трансляцию.
        Assert.Equal(TwitchStreamState.Unknown, fixture.Service.StreamState);
    }

    [Fact]
    public async Task LoggingOutStopsEverythingAndForgetsTheAccount()
    {
        var fixture = new Fixture(authorized: true);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";

        fixture.Service.ApplySettings();
        await fixture.Service.LogoutAsync();

        // После выхода не должно остаться ни подписки, ни признака входа: иначе окно
        // показывало бы «вошли» при отсутствии токена.
        Assert.True(fixture.Chat.Stopped);
        Assert.False(fixture.Service.IsAuthorized);
        Assert.Equal("", fixture.Service.AuthorizedLogin);
        Assert.Equal("", fixture.Settings.AuthorizedLogin);
        Assert.True(fixture.Auth.LoggedOut);
        fixture.Dispose();
    }

    [Fact]
    public async Task RestoringWithoutAStoredTokenDoesNothingAndDoesNotComplain()
    {
        using var fixture = new Fixture(authorized: false);

        await fixture.Service.RestoreAsync();

        // Вход не выполнялся — обычное состояние для большинства пользователей. Ошибкой
        // это считать нельзя, иначе при каждом запуске писалось бы в журнал.
        Assert.False(fixture.Service.IsAuthorized);
        Assert.Empty(fixture.Api.Requests);
    }

    [Fact]
    public async Task AFinishedLoginConnectsTheChatRightAway()
    {
        using var fixture = new Fixture(authorized: false);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";

        var token = Token();
        fixture.Auth.PendingToken = token;
        fixture.Auth.PollResult = new DeviceAuthResult(DeviceAuthStatus.Authorized, token);

        // Пользователю не должно требоваться после подтверждения входа ещё что-то
        // включать: он только что нажал «войти».
        var result = await fixture.Service.ContinueLoginAsync(Challenge());

        Assert.Equal(DeviceAuthStatus.Authorized, result.Status);
        Assert.Equal("reader", fixture.Service.AuthorizedLogin);
        Assert.Equal("reader", fixture.Settings.AuthorizedLogin);
        Assert.True(fixture.Chat.Started);
    }

    [Fact]
    public async Task AnUnconfirmedLoginDoesNotRecordAnAccount()
    {
        using var fixture = new Fixture(authorized: false);
        fixture.Auth.PendingToken = null;
        fixture.Auth.PollResult = new DeviceAuthResult(DeviceAuthStatus.Pending, null);

        await fixture.Service.ContinueLoginAsync(Challenge());

        // Пока пользователь не подтвердил вход, считать аккаунт подключённым нельзя: это
        // показалось бы в окне и ввело бы в заблуждение.
        Assert.Equal("", fixture.Settings.AuthorizedLogin);
    }

    [Fact]
    public async Task ARefusedLoginDoesNotRecordAnAccountEither()
    {
        using var fixture = new Fixture(authorized: false);
        fixture.Auth.PendingToken = null;
        fixture.Auth.PollResult = new DeviceAuthResult(DeviceAuthStatus.Denied, null);

        await fixture.Service.ContinueLoginAsync(Challenge());

        // Отказ — это решение пользователя, и оно окончательное. Считать аккаунт
        // подключённым после него — значит врать в окне.
        Assert.Equal("", fixture.Settings.AuthorizedLogin);
        Assert.False(fixture.Chat.Started);
    }

    [Fact]
    public void TheSpeechSourceIsNotRunningWithoutAConnection()
    {
        using var fixture = new Fixture(authorized: true);

        using var source = new TwitchTtsMessageSource(fixture.Service);

        // «Источник работает» без соединения означало бы, что реплики будут, а их не
        // будет. При разборе причин это выглядело бы как поломка модуля.
        Assert.False(source.IsRunning);
        Assert.Equal("twitch", source.Name);
    }

    [Fact]
    public void TheSpeechSourceIsRunningOnceTheChatIsConnected()
    {
        using var fixture = new Fixture(authorized: true);
        using var source = new TwitchTtsMessageSource(fixture.Service);

        fixture.Chat.Start("111", "999");

        Assert.True(source.IsRunning);
    }

    [Fact]
    public void TheSpeechSourceForwardsMessagesFromTheChat()
    {
        using var fixture = new Fixture(authorized: true);
        using var source = new TwitchTtsMessageSource(fixture.Service);

        var received = new List<TtsChatMessage>();
        source.MessageReceived += received.Add;

        fixture.Chat.Raise(new TwitchChatMessage("viewer", "!ttsvoice Irina"));

        // Проверяется только передача: разбор команд покрыт отдельно и повторять его
        // здесь незачем — именно ради этого мост и тонкий.
        Assert.Single(received);
        Assert.Equal("!ttsvoice Irina", received[0].Text);
    }

    [Fact]
    public void StartingTheSpeechSourceAppliesTheTwitchSettings()
    {
        using var fixture = new Fixture(authorized: true);
        using var source = new TwitchTtsMessageSource(fixture.Service);
        fixture.Settings.Enabled = true;
        fixture.Settings.ChannelLogin = "streamer";
        fixture.Settings.ChannelUserId = "111";

        source.Start(new TextToSpeechSettings());

        Assert.True(fixture.Chat.Started);
    }

    [Fact]
    public void StoppingTheSpeechSourceLeavesTheStreamStatusAlone()
    {
        using var fixture = new Fixture(authorized: true);
        using var source = new TwitchTtsMessageSource(fixture.Service);

        // Индикатор трансляции нужен и при выключенной озвучке: стример смотрит, идёт
        // ли эфир, независимо от того, озвучивает ли модуль чат. Рвать из-за этого
        // подключение нельзя.
        source.Stop();

        Assert.False(fixture.Chat.Stopped);
    }

    [Fact]
    public void DisposingTheSourceStopsForwardingMessages()
    {
        var fixture = new Fixture(authorized: true);
        var source = new TwitchTtsMessageSource(fixture.Service);
        var received = new List<TtsChatMessage>();
        source.MessageReceived += received.Add;

        source.Dispose();
        fixture.Chat.Raise(new TwitchChatMessage("viewer", "!tts после выгрузки"));

        // Освобождение источника происходит при выходе из приложения; подписка после
        // него должна исчезнуть, иначе обработчик модуля жил бы после окна.
        Assert.Empty(received);
        fixture.Dispose();
    }

    private static DeviceCodeChallenge Challenge() =>
        new("DC", "CODE", "https://twitch.tv/activate", 1800, 5);

    private static TwitchToken Token() => new()
    {
        AccessToken = "AAA",
        RefreshToken = "RRR",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(3),
        Login = "reader",
        UserId = "999",
    };

    /// <summary>
    /// Читатель чата без сокета.
    ///
    /// Здесь проверяются решения поверх чтения: когда подключаться, когда
    /// отключаться, что показывать. Транспорт проверяется отдельно и без сети — в
    /// проверках разбора кадров.
    /// </summary>
    private sealed class FakeChatReader : ITwitchChatReader
    {
        public bool IsConnected { get; private set; }
        public string LastError { get; set; } = "";
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public int Raised { get; private set; }

        public event Action<TwitchChatMessage>? MessageReceived;

        /// <summary>
        /// Подписчика здесь нет намеренно: интеграция его не использует, и объявлять
        /// событие ради подписи было бы уступкой форме, а не смыслу.
        /// </summary>
        public event Action<bool>? ConnectionChanged { add { } remove { } }

        public bool Start(string broadcasterUserId, string readerUserId)
        {
            Started = true;
            IsConnected = true;
            return true;
        }

        public void Stop()
        {
            Stopped = true;
            IsConnected = false;
        }

        /// <summary>Подсунуть реплику, как если бы она пришла из чата.</summary>
        public void Raise(TwitchChatMessage message)
        {
            Raised++;
            MessageReceived?.Invoke(message);
        }

        public void Dispose() => Stop();
    }

    /// <summary>Вход без сети: состояние задаёт проверка, запросы не уходят.</summary>
    private sealed class FakeAuth(TwitchToken? token) : ITwitchAuth
    {
        public TwitchToken? PendingToken { get; set; } = token;

        public bool IsAuthorized => PendingToken is not null;
        public string AuthorizedLogin => PendingToken?.Login ?? "";
        public string UserId => PendingToken?.UserId ?? "";
        public string? AccessToken => PendingToken?.AccessToken;

        /// <summary>Что вернул опрос кода. По умолчанию «пользователь ещё не подтвердил».</summary>
        public DeviceAuthResult PollResult { get; set; } = new(DeviceAuthStatus.Pending, null);

        public bool LoggedOut { get; private set; }

        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PendingToken is not null);

        public Task<DeviceCodeChallenge?> BeginLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DeviceCodeChallenge?>(Challenge());

        public Task<DeviceAuthResult> ContinueLoginAsync(DeviceCodeChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            // Пользователь подтвердил вход: токен становится текущим. Проверка задаёт
            // это заранее, потому что реальный обмен кода ходит в сеть.
            if (PollResult.Status == DeviceAuthStatus.Authorized && PendingToken is not null)
                PollResult = new DeviceAuthResult(DeviceAuthStatus.Authorized, PendingToken);

            return Task.FromResult(PollResult);
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            LoggedOut = true;
            PendingToken = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>API без сети: ответы задаёт проверка, обращения считаются.</summary>
    private sealed class FakeApiClient : ITwitchApi
    {
        public List<string> Requests { get; } = new();

        /// <summary>Ответ на «идёт ли трансляция». null — сеть недоступна.</summary>
        public bool? Live { get; set; }

        /// <summary>Ответ на «какой идентификатор у канала». null — канала нет.</summary>
        public string? UserId { get; set; } = "111";

        public int StreamQueries { get; private set; }
        public int UserIdQueries { get; private set; }

        public Task<bool?> IsLiveAsync(string channelLogin, CancellationToken cancellationToken = default)
        {
            StreamQueries++;
            Requests.Add("streams:" + channelLogin);
            return Task.FromResult(Live);
        }

        public Task<string?> ResolveChannelUserIdAsync(string channelLogin,
            CancellationToken cancellationToken = default)
        {
            UserIdQueries++;
            Requests.Add("users:" + channelLogin);
            return Task.FromResult(UserId);
        }
    }

    private sealed class DirectDispatcher : IDispatcherService
    {
        public bool HasThreadAccess => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class StubSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Settings { get; set; } = settings;
        public void Save() { }
    }

    /// <summary>
    /// Обстановка проверки: сервис и заглушки вокруг него. Настройки настоящие,
    /// потому что именно в них проверяется, что интеграция не включает себя сама.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(bool authorized)
        {
            AppSettings = new AppSettings();
            SettingsService = new StubSettingsService(AppSettings);
            Auth = new FakeAuth(authorized ? Token() : null);
            Api = new FakeApiClient();
            Chat = new FakeChatReader();
            Service = new TwitchService(Auth, Api, Chat, SettingsService, new DirectDispatcher());
        }

        public AppSettings AppSettings { get; }
        public TwitchSettings Settings => AppSettings.Twitch;
        public StubSettingsService SettingsService { get; }
        public FakeAuth Auth { get; }
        public FakeApiClient Api { get; }
        public FakeChatReader Chat { get; }
        public TwitchService Service { get; }

        /// <summary>
        /// Дождаться состояния с пределом. Без предела проверка ждала бы вечно и падала
        /// бы по таймауту всей сборки вместо внятного сообщения.
        /// </summary>
        public void WaitForStatus(Func<bool> reached, int timeoutMs = 5000)
        {
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                            + (long)(timeoutMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

            while (!reached())
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                    Assert.Fail($"Состояние не наступило за {timeoutMs} мс");

                Thread.Sleep(10);
            }
        }

        public void Dispose() => Service.Dispose();
    }
}