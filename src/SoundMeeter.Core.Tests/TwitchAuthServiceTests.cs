using System.Net;
using SoundMeeter.Services.Twitch;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Вход в Twitch по схеме device code (SM-F01).
///
/// Сеть не трогается: подменяется обработчик HTTP, и проверяется ровно то, что
/// важно для пользователя — приложение поймёт отказ Twitch и предложит войти
/// заново, а не молча перестанет читать чат.
///
/// Отдельно проверяется, что при обновлении токена сохраняются логин и
/// user_id: Twitch в ответе на обновление их не присылает, и потерянный user_id
/// означал бы, что подписка на чат больше не создаётся, хотя вход «прошёл».
/// </summary>
public class TwitchAuthServiceTests
{
    private const string Pending = "{\"status\":400,\"message\":\"authorization_pending\"}";
    private const string Denied = "{\"status\":400,\"message\":\"access_denied\"}";
    private const string Expired = "{\"status\":400,\"message\":\"expired_token\"}";
    private const string BadRefresh = "{\"status\":400,\"message\":\"Invalid refresh token\"}";
    private const string Unknown = "{\"status\":500,\"message\":\"boom\"}";

    private const string Token = "{\"access_token\":\"AAA\",\"refresh_token\":\"RRR\"," +
        "\"expires_in\":14400,\"token_type\":\"bearer\"}";
    private const string TokenNoUser = "{\"access_token\":\"AAA\",\"expires_in\":14400}";

    private const string Identity = "{\"data\":[{\"login\":\"reader\",\"id\":\"999\"}]}";
    private const string IdentityEmpty = "{\"data\":[]}";

    [Fact]
    public async Task AnUnregisteredClientIsReportedInsteadOfCalled()
    {
        // Пока client ID не подставлен в код, приложение обязано сказать об этом
        // само, а не сходить в сеть и не вернуть 401 от Twitch: пользователь должен
        // видеть причину, а не отказ чужого сервиса.
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна быть тронута"));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new TwitchAuthService(http, new MemoryStore(), "");

        Assert.Null(await auth.BeginAuthorizationAsync());
        Assert.False(await auth.RestoreAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheRegisteredClientIdIsReportedOnceItIsPasted()
    {
        // Обратная сторона той же проверки: константа в коде — единственное, что
        // превращает модуль из «не настроено» в рабочий. Тест напоминает, что она
        // обязана быть видна как признак готовности, а не как пустое поле.
        Assert.Equal(
            TwitchAuthService.IsConfigured,
            !string.IsNullOrWhiteSpace(TwitchAuthService.ClientId));
    }

    [Fact]
    public async Task PollingBeforeTheUserConfirmsKeepsWaiting()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, Pending));
        using var fixture = new Fixture(handler);
        var challenge = new DeviceCodeChallenge("DC", "CODE", "https://twitch.tv/activate", 1800, 5);

        var result = await fixture.Auth.PollForTokenAsync(challenge);

        // «Ждём» — не ошибка: пользователь ещё в браузере.
        Assert.Equal(DeviceAuthStatus.Pending, result.Status);
        Assert.Null(fixture.Store.Saved);
    }

    [Theory]
    [InlineData(Denied, DeviceAuthStatus.Denied)]
    [InlineData(Expired, DeviceAuthStatus.Expired)]
    [InlineData(Unknown, DeviceAuthStatus.Failed)]
    public async Task TwitchRefusalsAreUnderstoodAndNotRetriedForever(string body, DeviceAuthStatus expected)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, body));
        using var fixture = new Fixture(handler);

        var result = await fixture.Auth.PollForTokenAsync(
            new DeviceCodeChallenge("DC", "CODE", "https://twitch.tv/activate", 1800, 5));

        Assert.Equal(expected, result.Status);
        Assert.Null(fixture.Store.Saved);
    }

    [Fact]
    public async Task ConfirmationSavesTheTokenWithTheAccountIdentity()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host.StartsWith("id.twitch")
            ? Json(HttpStatusCode.OK, Token)
            : Json(HttpStatusCode.OK, Identity));
        using var fixture = new Fixture(handler);

        var result = await fixture.Auth.PollForTokenAsync(
            new DeviceCodeChallenge("DC", "CODE", "https://twitch.tv/activate", 1800, 5));

        Assert.Equal(DeviceAuthStatus.Authorized, result.Status);
        Assert.NotNull(result.Token);

        // Логин и user_id приходят не из ответа на обмен кода, а отдельным запросом:
        // без них подписка на чат не создаётся.
        Assert.Equal("reader", result.Token!.Login);
        Assert.Equal("999", result.Token.UserId);

        var saved = Assert.IsType<TwitchToken>(fixture.Store.Saved);
        Assert.Equal("AAA", saved.AccessToken);
        Assert.Equal("reader", fixture.Auth.AuthorizedLogin);
    }

    [Fact]
    public async Task ATokenWithoutAnAccountIsNotSaved()
    {
        // Токен без user_id — мёртвый: подписка на чат адресуется идентификатором.
        // Сохранить его означало бы показать «вход выполнен» и молча не получать
        // сообщений, поэтому такой вход считается неудачным.
        var handler = new StubHandler(request => request.RequestUri!.Host.StartsWith("id.twitch")
            ? Json(HttpStatusCode.OK, Token)
            : Json(HttpStatusCode.OK, IdentityEmpty));
        using var fixture = new Fixture(handler);

        var result = await fixture.Auth.PollForTokenAsync(
            new DeviceCodeChallenge("DC", "CODE", "https://twitch.tv/activate", 1800, 5));

        Assert.Equal(DeviceAuthStatus.Failed, result.Status);
        Assert.Null(fixture.Store.Saved);
    }

    [Fact]
    public async Task RefreshingKeepsTheLoginAndAccountId()
    {
        // Отдельная проверка: Twitch в ответе на обновление логин и user_id не
        // присылает, а без них токен бесполезен для подписки на чат. Потеря здесь
        // выглядела бы как «вход был, и перестал работать» через четыре часа.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Token));
        using var fixture = new Fixture(handler, token: ExpiringToken());

        // Путь, который и есть на самом деле: токен из файла почти истёк, значит
        // обновление происходит при восстановлении входа, а не отдельной командой.
        Assert.True(await fixture.Auth.RestoreAsync());

        var current = fixture.Auth.Current!;
        Assert.Equal("reader", current.Login);
        Assert.Equal("999", current.UserId);
        Assert.NotNull(fixture.Store.Saved);
    }

    [Fact]
    public async Task RefreshingWithAUsedUpTokenForcesALoginAgain()
    {
        // Обновляющий токен одноразовый: не принят — значит вход не восстановить.
        // Молча работать дальше нельзя, Twitch вернёт 401 на каждый запрос.
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, BadRefresh));
        using var fixture = new Fixture(handler, token: ExpiringToken());

        Assert.False(await fixture.Auth.RestoreAsync());
        Assert.True(fixture.Store.Cleared);
        Assert.Null(fixture.Auth.Current);
    }

    [Fact]
    public async Task ANetworkHiccupDoesNotThrowAwayAWorkingToken()
    {
        // Токен ещё жив, сеть моргнула. Стирать его — значит заставить пользователя
        // входить заново из-за секундной недоступности сети.
        var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
        using var fixture = new Fixture(handler, token: ExpiringToken());

        Assert.False(await fixture.Auth.RestoreAsync());

        // Локальный файл не тронут: токен ещё годен, следующая попытка его продлит.
        Assert.False(fixture.Store.Cleared);
    }

    [Fact]
    public async Task AValidStoredTokenIsAcceptedWithoutGoingOnline()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна быть тронута"));
        using var fixture = new Fixture(handler, token: FreshToken());

        Assert.True(await fixture.Auth.RestoreAsync());

        Assert.Empty(handler.Requests);
        Assert.Equal("reader", fixture.Auth.AuthorizedLogin);
    }

    [Fact]
    public async Task ASoonExpiringStoredTokenIsRefreshedAtStartup()
    {
        // Ждать, пока токен протухнет, означало бы обнаружить это уже на первом
        // сообщении из чата — то есть на эфире.
        var handler = new StubHandler(request => request.RequestUri!.Host.StartsWith("id.twitch")
            ? Json(HttpStatusCode.OK, Token)
            : Json(HttpStatusCode.OK, Identity));
        using var fixture = new Fixture(handler, token: ExpiringToken());

        Assert.True(await fixture.Auth.RestoreAsync());

        Assert.NotEmpty(handler.Requests);
    }

    [Fact]
    public async Task NoStoredTokenMeansNoLogin()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не нужна"));
        using var fixture = new Fixture(handler);

        Assert.False(await fixture.Auth.RestoreAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LoggingOutRevokesTheTokenAndForgetsIt()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var fixture = new Fixture(handler, token: FreshToken());

        await fixture.Auth.RestoreAsync();
        await fixture.Auth.LogoutAsync();

        // Без отзыва серверный токен продолжал бы работать, пока пользователь
        // считает, что вышел из аккаунта.
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal));
        Assert.True(fixture.Store.Cleared);
        Assert.Equal("", fixture.Auth.AuthorizedLogin);
    }

    [Fact]
    public async Task LogoutWorksEvenWhenTheNetworkIsGone()
    {
        // Недоступность сети не должна мешать выйти: локальный секрет всё равно
        // удаляется, а серверный токен протухнет сам.
        var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var fixture = new Fixture(handler, token: FreshToken());

        await fixture.Auth.RestoreAsync();
        await fixture.Auth.LogoutAsync();

        Assert.True(fixture.Store.Cleared);
        Assert.Equal("", fixture.Auth.AuthorizedLogin);
    }

    [Fact]
    public async Task ACancelledPollIsNotSwallowedAsPending()
    {
        // Отмена — это команда выйти, а не «пользователь ещё думает». Иначе выход
        // ждал бы полминуты впустую.
        var handler = new StubHandler(_ => throw new TaskCanceledException());
        using var fixture = new Fixture(handler);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Auth.PollForTokenAsync(
                new DeviceCodeChallenge("DC", "C", "u", 1800, 5), cancellation.Token));
    }

    [Fact]
    public async Task AValidChallengeComesBackWithTheCodeToShow()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"device_code\":\"DC\",\"user_code\":\"ABCD1234\"," +
            "\"verification_uri\":\"https://www.twitch.tv/activate\",\"expires_in\":1800,\"interval\":5}"));
        using var fixture = new Fixture(handler);

        var challenge = await fixture.Auth.BeginAuthorizationAsync();

        Assert.NotNull(challenge);
        Assert.Equal("ABCD1234", challenge!.UserCode);
        Assert.Contains("activate", challenge.VerificationUri);

        // Twitch задаёт интервал сам, и спрашивать чаще он не разрешает.
        Assert.Equal(5, challenge.IntervalSeconds);
    }

    [Fact]
    public async Task ARefusedChallengeIsNotShownToTheUser()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, Unknown));
        using var fixture = new Fixture(handler);

        Assert.Null(await fixture.Auth.BeginAuthorizationAsync());
    }

    private static TwitchToken FreshToken() => new()
    {
        AccessToken = "OLD",
        RefreshToken = "RRR",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Login = "reader",
        UserId = "999",
    };

    /// <summary>
    /// Токен, до истечения которого осталось меньше запаса: такой обновляется при
    /// восстановлении входа, а не используется как есть.
    /// </summary>
    private static TwitchToken ExpiringToken() => new()
    {
        AccessToken = "OLD",
        RefreshToken = "RRR",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        Login = "reader",
        UserId = "999",
    };

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>
    /// Подменяет сеть. Соответствует принятому в проекте приёму: никакого реального
    /// обращения, ответ задаёт проверка, а адреса запросов записываются, чтобы можно
    /// было утверждать, что второго адреса не касались.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class MemoryStore(TwitchToken? token = null) : ITwitchTokenStore
    {
        public TwitchToken? Saved { get; private set; }
        public bool Cleared { get; private set; }
        public TwitchToken? Load() => token;
        public void Save(TwitchToken value) => Saved = value;
        public void Clear() => Cleared = true;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(StubHandler handler, TwitchToken? token = null)
        {
            Store = new MemoryStore(token);
            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            // Фиктивный client ID: настоящий вписывается в код при регистрации
            // приложения в Twitch, и без него проверки разбора ответов были бы
            // недостижимыми — каждый выход вышел бы на «приложение не настроено».
            Auth = new TwitchAuthService(Http, Store, "testclientid");
        }

        public MemoryStore Store { get; }
        public HttpClient Http { get; }
        public TwitchAuthService Auth { get; }

        public void Dispose() => Http.Dispose();
    }
}