using System.Net;
using System.Text;
using SoundMeeter.Services.Twitch;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Разговор с Helix API Twitch (SM-F01).
///
/// Проверяется не «формат ответа», а то, что модуль ведёт себя прижизненно: не
/// бросает при сетевом сбое (это фоновый опрос на стриме), различает «канала нет»
/// и «сети нет», и правильно собирает заголовки, без которых Twitch отвечает 401
/// без внятной причины.
///
/// Отдельно проверяется «неизвестно» вместо «не в эфире»: офлайн-канал и недоступная
/// сеть — разные вещи, и показывать в строке состояния «канал офлайн» при обрыве
/// сети значило бы врать пользователю на его же стриме.
/// </summary>
public class TwitchApiClientTests
{
    private const string ClientId = "testclientid";
    private const string Token = "AAA";

    [Fact]
    public async Task ALiveChannelIsReportedAsLive()
    {
        using var fixture = new Fixture(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"type\":\"live\",\"title\":\"ok\",\"user_login\":\"streamer\"}]}"));

        Assert.True(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task AnOfflineChannelIsReportedAsOffline()
    {
        // У офлайн-канала Twitch возвращает пустой data, а не запись с признаком
        // «офлайн». Пустой массив — это достоверный ответ «не в эфире».
        using var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"));

        Assert.False(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task AStreamThatIsNotLiveYetPresentIsNotReportedAsLive()
    {
        // Запись без type: канал есть, трансляции нет. Считать это «в эфире» по
        // одному факту непустого массива значило бы показывать неверный индикатор.
        using var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{\"data\":[{\"title\":\"waiting\"}]}"));

        Assert.False(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task NetworkTroubleIsReportedAsUnknownAndNotAsOffline()
    {
        // Смысл различия: обрыв сети не должен выглядеть как «стрим закончился».
        // В первом случае модуль молчит о статусе, во втором — сказал бы пользователю
        // на его же трансляции, что он не в эфире.
        using var fixture = new Fixture(_ => throw new HttpRequestException("offline"));

        Assert.Null(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task AServerErrorIsAlsoUnknownRatherThanOffline()
    {
        using var fixture = new Fixture(_ => Json(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task AnExpiredLoginIsTreatedAsUnknownAndNotAsOffline()
    {
        // 401 — это истёкший вход, а не «канал офлайн». Различать обязательно: во
        // втором случае модуль сказал бы пользователю на его же трансляции, что тот
        // не в эфире, и предложил бы разбираться с несуществующей проблемой.
        using var fixture = new Fixture(_ => Json(HttpStatusCode.Unauthorized,
            "{\"status\":401,\"message\":\"Invalid OAuth token\"}"));

        Assert.Null(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task BrokenJsonDoesNotThrow()
    {
        // Фоновый опрос каждые полминуты: исключение здесь уронило бы поток.
        using var fixture = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
        });

        Assert.Null(await fixture.Api.IsLiveAsync("streamer"));
    }

    [Fact]
    public async Task BothRequiredHeadersAreSent()
    {
        // Без Client-Id или Authorization Twitch отвечает 401 без объяснений, и это
        // выглядело бы как поломка модуля.
        HttpRequestMessage? seen = null;
        var fixture = new Fixture(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, "{\"data\":[]}");
        });

        await fixture.Api.IsLiveAsync("streamer");

        Assert.NotNull(seen);
        Assert.Contains(seen!.Headers, h => h.Key == "Client-Id" && h.Value.Contains(ClientId));
        Assert.Equal("Bearer " + Token, seen.Headers.Authorization?.ToString());
        fixture.Dispose();
    }

    [Fact]
    public async Task WithoutALoginNoRequestIsSentAtAll()
    {
        // Обращаться с пустым логином бессмысленно: Twitch вернёт пустой data, и
        // приложение решило бы, что канала нет, хотя его просто не выбрали.
        var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"));

        Assert.Null(await fixture.Api.IsLiveAsync(""));
        Assert.Null(await fixture.Api.IsLiveAsync("   "));
        Assert.Empty(fixture.Handler.Requests);
        fixture.Dispose();
    }

    [Fact]
    public async Task WithoutATokenNoRequestIsSent()
    {
        var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"), token: null);

        Assert.Null(await fixture.Api.IsLiveAsync("streamer"));
        Assert.Empty(fixture.Handler.Requests);
        fixture.Dispose();
    }

    [Fact]
    public async Task TheChannelIdentifierIsResolvedByLogin()
    {
        // Подписка на чат адресуется идентификатором, а не логином: без этого шага
        // подписка не создаётся вовсе.
        using var fixture = new Fixture(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"123456789\",\"login\":\"streamer\"}]}"));

        Assert.Equal("123456789", await fixture.Api.ResolveChannelUserIdAsync("streamer"));
    }

    [Fact]
    public async Task AMissingChannelResolvesToNothingRatherThanAnEmptyId()
    {
        // Пустой идентификатор хуже отсутствия: он ушёл бы в подписку и Twitch
        // ответил бы 400 без указания на то, что не так на самом деле.
        using var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"));

        Assert.Null(await fixture.Api.ResolveChannelUserIdAsync("nobody"));
    }

    [Fact]
    public async Task TheChatSubscriptionNamesBothTheChannelAndTheReader()
    {
        // Тело запроса читается внутри обработчика: клиент освобождает его вместе
        // с запросом, и попытка прочитать его после возврата упала бы на
        // освобождённом объекте.
        string body = "";
        var fixture = Fixture.Async(async request =>
        {
            body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync();

            return Json(HttpStatusCode.NoContent, "");
        });

        Assert.True(await fixture.Api.SubscribeToChatAsync("BROADCASTER", "READER"));

        // Два разных идентификатора: канал, чей чат читаем, и аккаунт, от имени
        // которого читаем. Их путаница — подписка не туда.
        Assert.Contains("BROADCASTER", body, StringComparison.Ordinal);
        Assert.Contains("READER", body, StringComparison.Ordinal);
        Assert.Contains("channel.chat.message", body, StringComparison.Ordinal);
        Assert.Contains("websocket", body, StringComparison.Ordinal);
        fixture.Dispose();
    }

    [Fact]
    public async Task ARejectedSubscriptionIsReportedAsFailure()
    {
        // Молча не создать подписку — значит показать пользователю «подключено» при
        // полном отсутствии сообщений из чата.
        using var fixture = new Fixture(_ => Json(HttpStatusCode.BadRequest,
            "{\"status\":400,\"message\":\"The total number of subscriptions...\"}"));

        Assert.False(await fixture.Api.SubscribeToChatAsync("BROADCASTER", "READER"));
    }

    [Fact]
    public async Task AnUnreachableSubscriptionIsNotThrown()
    {
        using var fixture = new Fixture(_ => throw new HttpRequestException("offline"));

        Assert.False(await fixture.Api.SubscribeToChatAsync("BROADCASTER", "READER"));
    }

    [Fact]
    public async Task TokenValidationAcceptsALiveToken()
    {
        var fixture = new Fixture(_ => Json(HttpStatusCode.OK,
            "{\"client_id\":\"c\",\"login\":\"reader\",\"scopes\":[\"user:read:chat\"]}"));

        Assert.True(await fixture.Api.ValidateTokenAsync());
        fixture.Dispose();
    }

    [Fact]
    public async Task TokenValidationRejectsARefusedToken()
    {
        using var fixture = new Fixture(_ => Json(HttpStatusCode.Unauthorized, "{\"status\":401}"));

        Assert.False(await fixture.Api.ValidateTokenAsync());
    }

    [Fact]
    public async Task TokenValidationWithoutATokenIsFalseWithoutAsking()
    {
        var fixture = new Fixture(_ => Json(HttpStatusCode.OK, "{}"), token: null);

        Assert.False(await fixture.Api.ValidateTokenAsync());
        Assert.Empty(fixture.Handler.Requests);
        fixture.Dispose();
    }

    [Fact]
    public async Task TheTokenIsReadAtEachRequestNotOnlyOnce()
    {
        // Токен живёт четыре часа и обновляется на ходу. Клиент, получивший его в
        // конструкторе, через несколько часов ходил бы с мёртвым токеном и получал
        // 401 на каждом запросе.
        var sent = new List<string>();
        string? token = Token;

        var fixture = new Fixture(request =>
        {
            sent.Add(request.Headers.Authorization?.ToString() ?? "");
            return Json(HttpStatusCode.OK, "{\"data\":[{\"type\":\"live\"}]}");
        }, accessToken: () => token);

        await fixture.Api.IsLiveAsync("streamer");
        token = "REFRESHED";
        await fixture.Api.IsLiveAsync("streamer");

        Assert.Contains(Token, sent[0], StringComparison.Ordinal);
        Assert.Contains("REFRESHED", sent[1], StringComparison.Ordinal);
        fixture.Dispose();
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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

    /// <summary>
    /// Обработчик умеет и асинхронно читать тело запроса: клиент освобождает его
    /// вместе с запросом, поэтому прочитать тело после возврата нельзя.
    /// </summary>
    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return respond(request);
        }
    }

    private sealed class Fixture : IDisposable
    {
        /// <summary>Отвечает сразу. Для большинства проверок этого достаточно.</summary>
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond,
            string? token = Token, Func<string?>? accessToken = null)
            : this(respond is null ? null : new StubHandler(respond), token, accessToken) { }

        /// <summary>
        /// Отвечает асинхронно. Нужен там, где проверке надо прочитать тело запроса:
        /// клиент освобождает его вместе с запросом, и после возврата читать нельзя.
        /// </summary>
        public static Fixture Async(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
            string? token = Token, Func<string?>? accessToken = null) =>
            new(new AsyncStubHandler(respond), token, accessToken);

        private Fixture(HttpMessageHandler? handler, string? token, Func<string?>? accessToken)
        {
            Handler = new RequestLog(handler ?? throw new ArgumentNullException(nameof(handler)));
            Http = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(20) };
            Api = new TwitchApiClient(Http, ClientId, accessToken ?? (() => token));
        }

        public RequestLog Handler { get; }
        public HttpClient Http { get; }
        public TwitchApiClient Api { get; }

        public void Dispose() => Http.Dispose();
    }

    /// <summary>Запоминает адреса запросов, чтобы можно было утверждать, что адрес не трогали.</summary>
    private sealed class RequestLog(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }
}