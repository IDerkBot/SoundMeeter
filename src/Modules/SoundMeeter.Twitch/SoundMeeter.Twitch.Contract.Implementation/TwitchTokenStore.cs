using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Contract.Implementation;

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