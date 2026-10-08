using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Токен доступа Twitch: пара «токен + обновляющий токен» и срок.
///
/// Отдельный класс, а не три строки, потому что пара всегда ходит вместе:
/// обновляющий токен выдаётся вместе с доступным и без доступа не имеет смысла,
/// а разъезжающаяся пара означала бы, что при следующем обновлении токен молча
/// потеряется.
/// </summary>
public sealed class TwitchToken
{
    /// <summary>Токен для обращения к API. Короткоживущий: около четырёх часов.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// Токен для продления. Одноразовый: Twitch выдаёт новый при каждом обновлении,
    /// а использованный становится недействительным — это защита от кражи перехватом.
    /// Поэтому он меняется при каждом обновлении, а не переиспользуется.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    /// <summary>Момент, после которого доступный токен нельзя использовать (UTC).</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Логин аккаунта, под которым выдан токен. Для показа в интерфейсе.</summary>
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    /// <summary>Идентификатор пользователя по токену. Адресует подписку на чат.</summary>
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>
    /// Токен пригоден к использованию.
    ///
    /// Проверка с запасом <see cref="ExpiryLeeway"/>: обновление требует запроса в
    /// сеть, а если он не пройдёт, соединённое приложение останется без чата до
    /// следующей попытки. Полчаса запаса означают, что подключение переживёт
    /// кратковременную потерю сети и не рассыплется на пустом токене.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken)
        && ExpiresAt > DateTimeOffset.UtcNow + ExpiryLeeway;

    /// <summary>
    /// Запас до срока, после которого токен считается протухшим.
    ///
    /// Обновление требует запроса в сеть, а если он не пройдёт, соединённое
    /// приложение останется без чата до следующей попытки. Полчаса запаса
    /// означают, что подключение переживёт кратковременную потерю сети и не
    /// рассыплется на пустом токене.
    /// </summary>
    public static readonly TimeSpan ExpiryLeeway = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Хранилище пары токенов (SM-F01).
///
/// Отдельный файл, а НЕ поле в settings.json, по трём причинам, и все три важны.
///
/// Первая и главная: settings.json показывается в мастере настроек, переносится
/// между компьютерами и кладётся в резервную копию. Туда нельзя класть то, что
/// даёт доступ к аккаунту: файл, отправленный на форум, сделал бы чужой стрим
/// читающимся от вашего имени.
///
/// Вторая: файл настроек версионируется миграциями и импортируется между
/// установками. Токен не имеет смысла переносить — он выдан конкретной машине
/// конкретному пользователю Windows, и импорт сделал бы вид, что вход есть, а его
/// нет.
///
/// Третья: расшифровка требует пользователя Windows. Читать такой файл должен
/// только он — значит, шифровать нужно тем же, что делает это невозможным для
/// другого пользователя системы.
///
/// Шифрование — DPAPI (ProtectedData) с областью CurrentUser. Это не изобретение:
/// так защищают пароли в самой Windows, и никаких дополнительных зависимостей для
/// этого не нужно.
/// </summary>
public interface ITwitchTokenStore
{
    /// <summary>Прочитать пару токенов. null, если входа не было или файл не читается.</summary>
    TwitchToken? Load();

    /// <summary>Записать пару токенов, зашифровав содержимое.</summary>
    void Save(TwitchToken token);

    /// <summary>Удалить файл — выход из аккаунта.</summary>
    void Clear();
}

/// <summary>Хранилище токенов на файле с шифрованием средствами Windows.</summary>
public sealed class TwitchTokenFileStore : ITwitchTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    private readonly ILogger _logger = AppLog.For<TwitchTokenFileStore>();
    private readonly string _path;

    /// <summary>Ключ, которым шифруется содержимое. Не секрет: он защищает от других пользователей системы.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SoundMeeter.Twitch.Token.v1");

    public TwitchTokenFileStore() : this(DefaultPath()) { }

    /// <summary>
    /// Путь к файлу. Внутренний конструктор — для проверок: тесты не должны
    /// трогать настоящий файл пользователя.
    /// </summary>
    internal TwitchTokenFileStore(string path) => _path = path;

    /// <summary>
    /// Рядом с settings.json: пользователь удаляет настройки программы одним
    /// действием, и вместе с ними должен исчезнуть вход в Twitch.
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundMeeter", "twitch.token");

    public TwitchToken? Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return null;

                byte[] cipher = File.ReadAllBytes(_path);
                if (cipher.Length == 0) return null;

                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                var token = JsonSerializer.Deserialize<TwitchToken>(plain, JsonOptions);

                return token is { AccessToken.Length: > 0 } ? token : null;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException
                                           or UnauthorizedAccessException)
            {
                // Файл не расшифровывается — это не ошибка, с которой надо разбираться
                // пользователю: почти всегда это другой пользователь Windows, другая
                // копия настроек или восстановление из backup. Молча просим войти
                // заново и убираем испорченный файл, чтобы он не мешал и не копился.
                _logger.LogWarning(ex, "Файл токена Twitch не читается ({Message}) — будет выполнен новый вход", ex.Message);
                TryDelete();
                return null;
            }
        }
    }

    public void Save(TwitchToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (_sync)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions);
            byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Сначала во временный файл рядом, потом переименование: обрыв записи
            // посреди процесса оставил бы читаемый файл с обрезанным токеном, и
            // следующий запуск решил бы, что вход был, а токена нет.
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            TryDelete();
            // Временный файл мог остаться от прерванной записи, и его содержимое —
            // тот же секрет в открытом виде.
            TryDelete(_path + ".tmp");
        }
    }

    private void TryDelete(string? path = null)
    {
        try
        {
            string target = path ?? _path;
            if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл занят антивирусом или ещё чем-то. Не повод ронять приложение:
            // в следующий раз перезапишем, а пользователь при выходе из аккаунта
            // увидит, что придётся войти заново.
            _logger.LogDebug(ex, "Не удалось удалить файл токена Twitch: {Message}", ex.Message);
        }
    }
}