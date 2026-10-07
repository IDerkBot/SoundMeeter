using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Вход в Twitch по схеме device code (SM-F01).
///
/// ПОЧЕМУ ИМЕННО ЭТА СХЕМА. Twitch предлагает четыре: неявную (редирект в браузере
/// и разбор <c>#access_token</c> из адресной строки), код авторизации (нужен
/// локальный HTTP-сервер и обработка редиректа), client credentials (это серверный
/// сценарий, аккаунт пользователя не даёт) и device code. Последняя рассчитана
/// ровно на наш случай — десктопное приложение на Windows без своего сервера:
/// приложение показывает код, пользователь вводит его на странице Twitch и
/// возвращается, а приложение тем временем опрашивает сервер по таймеру. Ни
/// порта, ни обработки редиректа, ни окна, которое надо закрыть руками.
///
/// Публичный клиент, без секрета. Секрет в десктопном приложении хранить негде:
/// он всё равно лежит на диске у пользователя, поэтому Twitch и предлагает для
/// таких случаев схему, которая секрета не требует.
/// </summary>
public sealed class TwitchAuthService : ITwitchAuth
{
    /// <summary>
    /// Client ID приложения SoundMeeter.
    ///
    /// ПУСТОЙ — это не ошибка сборки, а место для вашего значения. Client ID
    /// выдаёт Twitch при регистрации приложения, и он публичен по своей сути:
    /// его нельзя спрятать в приложении, которое пользователь запускает на своём
    /// компьютере. Ровно поэтому он лежит здесь, одной строкой, а не в настройках.
    ///
    /// Как получить: twitch.tv/console → Applications → Register an Application →
    /// заполнить имя и категорию → Copy Client ID → вставить ниже и пересобрать.
    /// Для этой схемы секрет не нужен, redirect URI — тоже.
    ///
    /// Пока строка пустая, модуль честно говорит в окне «не настроено» и в сеть не
    /// ходит: так пользователь увидит причину, а не ошибку 401 от Twitch.
    /// </summary>
    public const string ClientId = "";

    private const string DeviceCodeUrl = "https://id.twitch.tv/oauth2/device";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string RevokeUrl = "https://id.twitch.tv/oauth2/revoke";

    /// <summary>Области прав. Ровно те, что нужны, и ни одной сверху.</summary>
    private const string Scopes = "user:read:chat channel:read:chat";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ITwitchTokenStore _store;
    private readonly string _clientId;
    private readonly ILogger _logger = AppLog.For<TwitchAuthService>();

    private TwitchToken? _current;

    public TwitchAuthService(HttpClient http, ITwitchTokenStore store)
        : this(http, store, ClientId) { }

    /// <summary>
    /// Конструктор с явным client ID.
    ///
    /// Нужен не только проверкам. Пока <see cref="ClientId"/> пуст, вся логика входа
    /// была бы недостижимой: каждый выход проверял бы «приложение настроено?» и
    /// выходил, а разбор ответов Twitch — отказов, ожидания, обновления — остался бы
    /// непроверенным до того момента, когда кто-то вставит настоящий ID. Идентификатор
    /// поэтому задаётся снаружи, а проверки подставляют фиктивный.
    /// </summary>
    internal TwitchAuthService(HttpClient http, ITwitchTokenStore store, string clientId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clientId = clientId ?? "";

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("SoundMeeter", "TTS"));
        }

        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Приложение настроено: есть client ID.</summary>
    public static bool IsConfigured => ClientId.Length > 0;

    /// <summary>Client ID этого экземпляра. Нужен клиенту API, который ходит от его имени.</summary>
    internal string EffectiveClientId => _clientId;

    /// <summary>У этого экземпляра есть client ID. То же, что <see cref="IsConfigured"/>, но про конкретный.</summary>
    private bool HasClientId => _clientId.Length > 0;

    /// <summary>Текущая пара токенов. null, если входа не было.</summary>
    public TwitchToken? Current => _current;

    /// <summary>Вход выполнен: токен есть и им можно пользоваться.</summary>
    public bool IsAuthorized => _current is not null;

    /// <summary>Идентификатор аккаунта по токену. Пусто — вход не выполнен.</summary>
    public string UserId => _current?.UserId ?? "";

    /// <summary>Текущий токен для обращения к API. Пусто — вход не выполнен.</summary>
    public string? AccessToken => _current?.AccessToken;

    /// <summary>Логин, под которым выполнен вход. Пусто — не выполнен.</summary>
    public string AuthorizedLogin => _current?.Login ?? "";

    /// <summary>
    /// Применить сохранённый токен, обновив его при необходимости.
    ///
    /// Вызывается при старте. Токен, который скоро истечёт, обновляется сразу:
    /// ждать, пока он протухнет, означало бы обнаружить это уже при первом
    /// сообщении из чата.
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var stored = _store.Load();
        if (stored is null)
        {
            _current = null;
            return false;
        }

        if (stored.IsUsable)
        {
            _current = stored;
            _logger.LogInformation("Twitch: вход восстановлен как {Login}", stored.Login);
            return true;
        }

        if (string.IsNullOrEmpty(stored.RefreshToken))
        {
            _current = null;
            return false;
        }

        var refreshed = await TryRefreshAsync(stored, cancellationToken).ConfigureAwait(false);
        _current = refreshed;
        return refreshed is not null;
    }

public async Task<DeviceCodeChallenge?> BeginLoginAsync(CancellationToken cancellationToken = default) =>
        await BeginAuthorizationAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    public async Task<DeviceAuthResult> ContinueLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default) =>
        await PollForTokenAsync(challenge, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Шаг первый: запросить код для входа.
    ///
    /// Возвращает то, что надо показать пользователю: код для ввода и страницу, где
    /// его вводить. Дальше приложение обязано опрашивать сервер по
    /// <paramref name="intervalSeconds"/> — чаще Twitch не отвечает и вернёт ошибку.
    /// </summary>
    public async Task<DeviceCodeChallenge?> BeginAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (!HasClientId) return null;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scopes"] = Scopes,
        });

        var response = await _http.PostAsync(DeviceCodeUrl, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Twitch: запрос кода входа отклонён ({Status})", (int)response.StatusCode);
            return null;
        }

        var challenge = await response.Content
            .ReadFromJsonAsync<DeviceCodeResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (challenge is null || string.IsNullOrEmpty(challenge.DeviceCode)) return null;

        int expiresIn = challenge.ExpiresIn > 0 ? challenge.ExpiresIn : 1800;

        return new DeviceCodeChallenge(
            challenge.DeviceCode!,
            challenge.UserCode ?? "",
            challenge.VerificationUri ?? "",
            expiresIn,
            challenge.Interval > 0 ? challenge.Interval : 5)
        {
            Deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
        };
    }

    /// <summary>
    /// Шаг второй: спросить, авторизовал ли пользователь приложение.
    ///
    /// Возвращает <see cref="DeviceAuthStatus.Pending"/>, пока пользователь не
    /// подтвердил вход, — это нормальный ответ, а не ошибка, и вызывающий должен
    /// спрашивать снова. <see cref="DeviceAuthStatus.Authorized"/> означает, что
    /// токен получен и уже сохранён.
    /// </summary>
    public async Task<DeviceAuthResult> PollForTokenAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        if (!HasClientId || string.IsNullOrEmpty(challenge.DeviceCode))
            return new DeviceAuthResult(DeviceAuthStatus.Failed, null);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scopes"] = Scopes,
            ["device_code"] = challenge.DeviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Twitch: опрос кода входа не дошёл: {Message}", ex.Message);
            return new DeviceAuthResult(DeviceAuthStatus.Pending, null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

                return error switch
                {
                    // Пользователь ещё не подтвердил — ждём и спрашиваем снова.
                    "authorization_pending" or "slow_down"
                        => new DeviceAuthResult(DeviceAuthStatus.Pending, null),

                    // Пользователь закрыл страницу и отказал. Это его решение,
                    // и оно окончательное: повторные попытки не помогут.
                    "access_denied"
                        => new DeviceAuthResult(DeviceAuthStatus.Denied, null),

                    // Код одноразовый и живёт ограниченное время.
                    "expired_token" or "invalid device code"
                        => new DeviceAuthResult(DeviceAuthStatus.Expired, null),

                    _ => new DeviceAuthResult(DeviceAuthStatus.Failed, null),
                };
            }

            var token = await response.Content
                .ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return await AdoptAsync(token, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Продлить токен. Вызывается перед тем, как он истечёт.
    ///
    /// Возвращает false, если продлить не вышло: вызывающий не должен продолжать
    /// работать с протухшим токеном, потому что Twitch вернёт 401, и это выглядело
    /// бы как поломка модуля, а не как истёкший вход.
    /// </summary>
    public async Task<bool> EnsureFreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_current is null) return false;
        if (_current.IsUsable) return true;

        _current = await TryRefreshAsync(_current, cancellationToken).ConfigureAwait(false);
        return _current is not null;
    }

    /// <summary>
    /// Выйти из аккаунта: отозвать токены у Twitch и удалить локальные.
    ///
    /// Отзыв нужен не из вежливости: без него токен продолжал бы работать, пока
    /// пользователь считает, что вышел. Сбой сети при отзыве молча переживаем —
    /// локальный файл всё равно удаляется, а серверный токен протухнет сам.
    /// </summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = _current;

        if (token is not null && HasClientId && !string.IsNullOrEmpty(token.AccessToken))
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["token"] = token.AccessToken,
                });

                using var response = await _http.PostAsync(RevokeUrl, content, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    _logger.LogDebug("Twitch: отзыв токена не подтверждён ({Status})", (int)response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Twitch: отзыв токена не дошёл: {Message}", ex.Message);
            }
        }

        _current = null;
        _store.Clear();
        _logger.LogInformation("Twitch: выполнен выход");
    }

    private async Task<TwitchToken?> TryRefreshAsync(TwitchToken previous, CancellationToken cancellationToken)
    {
        if (!HasClientId) return null;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["refresh_token"] = previous.RefreshToken,
                ["grant_type"] = "refresh_token",
            });

            using var response = await _http.PostAsync(TokenUrl, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Обновляющий токен одноразовый: если Twitch его не принял, он либо
                // уже использован, либо вход отозван. Оба случая требуют нового
                // входа, поэтому молча перестаём работать и просим войти заново.
                _logger.LogWarning("Twitch: не удалось обновить токен ({Status}) — нужен новый вход",
                    (int)response.StatusCode);
                _store.Clear();
                return null;
            }

            var token = await response.Content
                .ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            // При обновлении Twitch не присылает логин и user_id — они уже известны
            // из прошлой пары. Без них обновлённый токен оказался бы бесполезным
            // для подписки на чат, поэтому переносим их из прошлого.
            var refreshed = new TokenResponse
            {
                AccessToken = token?.AccessToken,
                RefreshToken = token?.RefreshToken,
                ExpiresIn = token?.ExpiresIn ?? 0,
                Login = previous.Login,
                UserId = previous.UserId,
            };

            var result = await AdoptAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return result.Status == DeviceAuthStatus.Authorized ? result.Token : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Twitch: обновление токена не дошло: {Message}", ex.Message);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Таймаут — это тоже «сеть не дошла». Стирать токен нельзя: он ещё жив,
            // и следующая попытка его продлит.
            _logger.LogDebug("Twitch: обновление токена не уложилось в отведённое время");
            return null;
        }
    }

    /// <summary>Сохранить полученную пару и узнать логин и идентификатор аккаунта.</summary>
    private async Task<DeviceAuthResult> AdoptAsync(TokenResponse? response, CancellationToken cancellationToken)
    {
        if (response is null || string.IsNullOrEmpty(response.AccessToken))
            return new DeviceAuthResult(DeviceAuthStatus.Failed, null);

        // Запрос аккаунта нужен только когда логина и user_id ещё нет, то есть при
        // первом входе. При обновлении они перенесены из прошлой пары, и лишний
        // запрос в сеть был бы лишним запросом каждые четыре часа.
        string login = response.Login ?? "";
        string userId = response.UserId ?? "";

        if (string.IsNullOrEmpty(userId))
        {
            var identity = await ResolveIdentityAsync(response.AccessToken, cancellationToken)
                .ConfigureAwait(false);

            login = identity?.Login ?? "";
            userId = identity?.UserId ?? "";
        }

        var token = new TwitchToken
        {
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken ?? "",
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(
                response.ExpiresIn > 0 ? response.ExpiresIn : 14400),
            Login = login,
            UserId = userId,
        };

        // Токен без user_id подписку на чат не создаст: она адресуется
        // идентификатором. Сохранять такой бессмысленно — лучше попросить войти
        // заново, когда API отвечает.
        if (string.IsNullOrEmpty(token.UserId))
        {
            _logger.LogWarning("Twitch: вход выполнен, но идентификатор аккаунта получить не удалось");
            return new DeviceAuthResult(DeviceAuthStatus.Failed, null);
        }

        _store.Save(token);
        _current = token;

        _logger.LogInformation("Twitch: выполнен вход как {Login}", token.Login);
        return new DeviceAuthResult(DeviceAuthStatus.Authorized, token);
    }

    /// <summary>
    /// Узнать логин и user_id по выданному токену.
    ///
    /// Отдельный запрос обязателен: в ответе на обмен кода этих данных нет, а
    /// подписка на чат адресуется именно идентификатором.
    /// </summary>
    private async Task<TwitchIdentity?> ResolveIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
            request.Headers.Add("Authorization", "Bearer " + accessToken);
            request.Headers.Add("Client-Id", _clientId);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return null;
            }

            var user = data[0];
            string login = user.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            string id = user.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";

            return string.IsNullOrEmpty(id) ? null : new TwitchIdentity(login, id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Twitch: не удалось узнать аккаунт по токену: {Message}", ex.Message);
            return null;
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return error?.Message;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Логин и идентификатор аккаунта по токену.</summary>
public sealed record TwitchIdentity(string Login, string UserId);

/// <summary>
/// Код входа, который надо показать пользователю, и секретная его часть.
///
/// <see cref="DeviceCode"/> в интерфейс не показывается: его не вводят руками, его
/// приложение само предъявляет серверу при обмене. Пользователь видит только
/// <see cref="UserCode"/> — короткий и переносимый с экрана на клавиатуру.
/// </summary>
public sealed record DeviceCodeChallenge(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int IntervalSeconds)
{
    /// <summary>Время жизни кода. По нему вызывающий прекращает опрос.</summary>
    public TimeSpan Lifetime => TimeSpan.FromSeconds(ExpiresIn);

    /// <summary>Как часто сервер разрешает спрашивать.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>
    /// Когда код перестанет работать. Нужен, чтобы опрос не шёл вечно: без срока
    /// пользователь, закрывший страницу и ушедший, оставил бы приложение
    /// опрашивать сервер до бесконечности.
    /// </summary>
    public DateTimeOffset Deadline { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Код ещё можно предъявлять серверу.</summary>
    public bool IsAlive => DateTimeOffset.UtcNow < Deadline;
}

/// <summary>Ответ на попытку забрать токен: состояние и, при успехе, сама пара.</summary>
public sealed record DeviceAuthResult(DeviceAuthStatus Status, TwitchToken? Token);

/// <summary>Что ответил сервер на попытку забрать токен.</summary>
public enum DeviceAuthStatus
{
    /// <summary>Пользователь ещё не подтвердил. Ждём и спрашиваем снова.</summary>
    Pending,

    /// <summary>Токен получен и сохранён.</summary>
    Authorized,

    /// <summary>Пользователь отказал.</summary>
    Denied,

    /// <summary>Код истёк или был уже использован.</summary>
    Expired,

    /// <summary>Что-то пошло не так: сеть, ответ без токена, нет user_id.</summary>
    Failed,
}

/// <summary>Ответ на запрос кода входа.</summary>
internal sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
}

/// <summary>Ответ на обмен кода или обновляющего токена.</summary>
internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    /// <summary>
    /// Логин и идентификатор аккаунта. Twitch присылает их только в отдельном
    /// запросе по готовому токену, поэтому при обновлении их переносят из прошлой
    /// пары — см. обновление токена в <see cref="TwitchAuthService"/>.
    /// </summary>
    public string? Login { get; set; }

    /// <summary>Идентификатор аккаунта. Адресует подписку на чат.</summary>
    public string? UserId { get; set; }
}

/// <summary>Ответ с ошибкой от Twitch.</summary>
internal sealed class ErrorResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}