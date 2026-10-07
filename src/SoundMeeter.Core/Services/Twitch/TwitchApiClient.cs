using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Разговор с Helix API Twitch (SM-F01): кто это, канал это или нет, идёт ли
/// трансляция.
///
/// Отдельный класс от <see cref="TwitchAuthService"/> намеренно: там — вход и
/// токены, здесь — запросы от лица уже вошедшего. Смешанное в одном классе
/// означало бы, что смена логина обновления токена может задеть разбор ответа API
/// и наоборот.
///
/// Каждый метод возвращает значение или null, а НЕ бросает: сетевой сбой на фоне
/// стрима не должен ронять модуль и не должен выглядеть как поломка. Причина
/// отказа пишется в журнал и показывается в строке состояния.
/// </summary>
public sealed class TwitchApiClient : ITwitchApi
{
    private const string ApiBase = "https://api.twitch.tv/helix";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly Func<string?> _accessToken;
    private readonly ILogger _logger = AppLog.For<TwitchApiClient>();

    public TwitchApiClient(HttpClient http, TwitchAuthService auth)
        : this(http, auth.EffectiveClientId, () => auth.Current?.AccessToken) { }

    /// <summary>
    /// Конструктор с явными зависимостями. Токен берётся функцией, а не значением:
    /// он обновляется на протяжении работы приложения, и клиент, получивший его
    /// один раз в конструкторе, через четыре часа ходил бы с мёртвым токеном.
    /// </summary>
    internal TwitchApiClient(HttpClient http, string clientId, Func<string?> accessToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clientId = clientId ?? "";
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SoundMeeter", "TTS"));

        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>Идёт ли трансляция у канала. null — неизвестно (сети нет или канала нет).</summary>
    public async Task<bool?> IsLiveAsync(string channelLogin, CancellationToken cancellationToken = default)
    {
        var stream = await GetSingleAsync("streams", "user_login", channelLogin, cancellationToken)
            .ConfigureAwait(false);

        // Отказ запроса — «неизвестно», а пустой data — достоверное «не в эфире».
        // Различать обязательно: недоступная сеть не должна выглядеть на эфире как
        // завершившаяся трансляция.
        if (!stream.Answered) return null;

        return stream.Element is { } element
               && element.TryGetProperty("type", out var type)
               && string.Equals(type.GetString(), "live", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Идентификатор канала по логину. null — канала нет, логина нет или сети нет.
    ///
    /// Нужен потому, что подписка на чат адресуется идентификатором, а не логином.
    /// </summary>
    public async Task<string?> ResolveChannelUserIdAsync(string channelLogin, CancellationToken cancellationToken = default)
    {
        var user = await GetSingleAsync("users", "login", channelLogin, cancellationToken)
            .ConfigureAwait(false);

        if (user.Element is not { } element || !element.TryGetProperty("id", out var id)) return null;

        return id.GetString();
    }

    /// <summary>
    /// Создать подписку на сообщения чата.
    ///
    /// <paramref name="broadcasterUserId"/> — канал, чат которого читаем,
    /// <paramref name="moderatorUserId"/> — аккаунт, от имени которого читаем
    /// (тот, под которым вошли). Это разные идентификаторы, и их путаница означала
    /// бы подписку не туда.
    /// </summary>
    public async Task<bool> SubscribeToChatAsync(string broadcasterUserId, string moderatorUserId,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = "channel.chat.message",
            ["version"] = "1",
            ["condition"] = new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterUserId,
                ["user_id"] = moderatorUserId,
            },
            // Способ доставки указывается в WebSocket-сессии, а не здесь: EventSub
            // требует указать transport, и для websocket в нём только method.
            ["transport"] = new Dictionary<string, string> { ["method"] = "websocket" },
        };

        return await PostAsync("eventsub/subscriptions", payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Проверить, что токен ещё жив. false — нужен новый вход.</summary>
    public async Task<bool> ValidateTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accessToken()) || _clientId.Length == 0) return false;

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Twitch: проверка токена не удалась: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Взять первый элемент массива <c>data</c> из ответа Helix.
    ///
    /// Ответ — это всегда конверт с массивом <c>data</c>, и нужный элемент там
    /// первый и единственный: запросы идут по одному логину.
    ///
    /// <see cref="SingleResult.Answered"/> отделяет «сервер ответил, и данных нет»
    /// от «не спросили или не дождались». Для вызывающих это разные вещи: пустой
    /// data про офлайн-канал — достоверный факт, а сетевой сбой — отсутствие
    /// факта, и показывать вместо второго первое значило бы врать на эфире.
    /// </summary>
    private async Task<SingleResult> GetSingleAsync(string endpoint, string parameter, string value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return SingleResult.Unknown;

        var token = _accessToken();
        if (string.IsNullOrEmpty(token) || _clientId.Length == 0)
        {
            _logger.LogDebug("Twitch: запрос «{Endpoint}» без токена", endpoint);
            return SingleResult.Unknown;
        }

        var url = $"{ApiBase}/{endpoint}?{parameter}={Uri.EscapeDataString(value)}";

        try
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Twitch: доступ к «{Endpoint}» отклонён ({Status}) — нужен новый вход",
                    endpoint, (int)response.StatusCode);
                return SingleResult.Unknown;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Twitch: «{Endpoint}» вернул {Status}", endpoint, (int)response.StatusCode);
                return SingleResult.Unknown;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                // Ответ получен, записей нет: канала или трансляции не существует.
                return SingleResult.Empty;
            }

            return new SingleResult(true, data[0].Clone());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Twitch: «{Endpoint}» не ответил: {Message}", endpoint, ex.Message);
            return SingleResult.Unknown;
        }
    }

    /// <summary>
    /// Результат запроса за одной записью. <see cref="Answered"/> отделяет «ответили,
    /// но пусто» от «не ответили».
    /// </summary>
    private readonly record struct SingleResult(bool Answered, JsonElement? Element = null)
    {
        public static SingleResult Unknown { get; } = new(false);
        public static SingleResult Empty { get; } = new(true);
    }

    private async Task<bool> PostAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        var token = _accessToken();
        if (string.IsNullOrEmpty(token) || _clientId.Length == 0) return false;

        try
        {
            using var request = CreateRequest(HttpMethod.Post, $"{ApiBase}/{endpoint}");
            request.Content = JsonContent.Create(payload);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;

            _logger.LogWarning("Twitch: подписка на чат не создана ({Status})", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Twitch: подписка на чат не дошла: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Запрос с заголовками, без которых Twitch отвечает 401.
    ///
    /// Client-Id обязателен всегда, а Authorization — всегда там, где нужен
    /// пользовательский доступ. Их отсутствие даёт отказ без внятной причины,
    /// поэтому собираются здесь, а не в каждом методе.
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        if (_clientId.Length > 0) request.Headers.Add("Client-Id", _clientId);

        string? token = _accessToken();
        if (!string.IsNullOrEmpty(token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        return request;
    }
}