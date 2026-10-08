using SoundMeeter.Services.Twitch;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Хранение пары токенов Twitch (SM-F01).
///
/// Проверяется на настоящем файле во временной папке, потому что здесь важно само
/// шифрование и то, что файл не переживает повреждение: незашифрованный токен в
/// settings.json означал бы, что он уедет вместе с мастером настроек и резервной
/// копией.
///
/// Файл настоящий, а не заглушка ещё и потому, что сбой расшифровки обязан
/// приводить к тихому «войдите заново», а не к падению приложения при старте.
/// </summary>
public class TwitchTokenStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public TwitchTokenStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SoundMeeterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "twitch.token");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Проверка не должна падать из-за того, что файл ещё держит антивирус.
        }
    }

    [Fact]
    public void ATokenSurvivesAFullWriteAndReadCycle()
    {
        var store = new TwitchTokenFileStore(_path);
        var token = Sample();

        store.Save(token);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("AAA", loaded!.AccessToken);
        Assert.Equal("RRR", loaded.RefreshToken);
        Assert.Equal("reader", loaded.Login);
        Assert.Equal("999", loaded.UserId);
    }

    [Fact]
    public void TheTokenIsNotReadableAsPlainText()
    {
        // Прямая проверка главного свойства: содержимое файла не является JSON с
        // токеном внутри. Читать его должен только пользователь Windows, под которым
        // файл создан.
        var store = new TwitchTokenFileStore(_path);

        store.Save(Sample());

        string onDisk = File.ReadAllText(_path);
        Assert.DoesNotContain("AAA", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("RRR", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("reader", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentFileMeansNoLogin()
    {
        Assert.Null(new TwitchTokenFileStore(_path).Load());
    }

    [Fact]
    public void AnEmptyFileMeansNoLogin()
    {
        // Обрыв записи может оставить файл нулевой длины. Это не ошибка чтения, а
        // отсутствие входа: приложение обязано предложить войти, а не упасть.
        File.WriteAllBytes(_path, Array.Empty<byte>());

        Assert.Null(new TwitchTokenFileStore(_path).Load());
    }

    [Fact]
    public void DamagedContentIsTreatedAsNoLoginAndRemoved()
    {
        // Файл, который не расшифровывается, почти всегда означает другой
        // пользователь Windows или восстановление из резервной копии. Проверка, что
        // он не бросает исключение при старте и не остаётся мусором на диске.
        File.WriteAllText(_path, "{\"access_token\":\"AAA\"}");

        Assert.Null(new TwitchTokenFileStore(_path).Load());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void ClearingRemovesTheTokenFromDisk()
    {
        var store = new TwitchTokenFileStore(_path);
        store.Save(Sample());

        store.Clear();

        Assert.False(File.Exists(_path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void ClearingAlsoRemovesTheTemporaryFileFromAnInterruptedWrite()
    {
        // Прерванная запись оставляет .tmp с тем же секретом в открытом виде.
        // Удаление только основного файла оставило бы его лежать на диске.
        var store = new TwitchTokenFileStore(_path);
        store.Save(Sample());
        File.WriteAllText(_path + ".tmp", "AAA");

        store.Clear();

        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void SavingTwiceReplacesTheTokenInsteadOfAppending()
    {
        var store = new TwitchTokenFileStore(_path);
        store.Save(Sample());
        store.Save(Sample(accessToken: "BBB"));

        Assert.Equal("BBB", store.Load()!.AccessToken);
    }

    [Fact]
    public void AFreshTokenIsUsableAndAnExpiringOneIsNot()
    {
        Assert.True(Sample(expiry: TimeSpan.FromHours(1)).IsUsable);

        // Протухание проверяется с запасом: обновление требует запроса в сеть, и
        // токен, до которого осталось 5 минут, может не пережить его.
        Assert.False(Sample(expiry: TimeSpan.FromMinutes(5)).IsUsable);
    }

    [Fact]
    public void ATokenWithoutARefreshPartIsUnusable()
    {
        // Без обновляющего токена пара не восстановится после перезапуска, а значит
        // «вход» был бы одноразовым — модуль отвалился бы через четыре часа.
        Assert.False(Sample(refreshToken: "").IsUsable);
        Assert.False(Sample(accessToken: "").IsUsable);
    }

    private static TwitchToken Sample(
        string accessToken = "AAA",
        string refreshToken = "RRR",
        TimeSpan? expiry = null) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = DateTimeOffset.UtcNow + (expiry ?? TimeSpan.FromHours(4)),
        Login = "reader",
        UserId = "999",
    };
}