using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Net;
using System.Text.Json;
using Xunit;

namespace SoundMeeter.Update.Tests;

/// <summary>
/// Цепочка релизов при обновлении через несколько версий (SM-E03).
///
/// Обновление может перескакивать с 0.0.1 сразу на 0.0.7, и показывать пользователю
/// записи только последнего релиза — значит молча выбросить то, ради чего он
/// обновляется. Здесь проверяется и отбор релизов, и то, что окно обновления
/// получает их все.
///
/// Настоящий <see cref="GithubUpdateService"/> работает здесь с заглушкой HttpClient:
/// отбор вынесен в чистую функцию, а второй запрос проверяется на уровне обмена —
/// так видно и «список не пришёл», и «установленная версия актуальна».
/// </summary>
public class UpdateChangelogTests
{
    private static GitHubReleaseDto Release(string tag, string body = "что-то поправили",
        bool draft = false, bool prerelease = false) => new()
    {
        TagName = tag,
        Name = "Version " + tag,
        Body = body,
        HtmlUrl = "https://github.com/a/b/releases/tag/" + tag,
        PublishedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
        Draft = draft,
        Prerelease = prerelease,

        // Portable-zip есть у настоящих релизов; без него окно обновления не может
        // предложить установку, и это проверяется здесь же.
        Assets =
        {
            new GitHubAssetDto
            {
                Name = $"SoundMeeter_{tag}.zip",
                Size = 2_680_614,
                BrowserDownloadUrl = "https://github.com/a/b/releases/download/" + tag + "/app.zip"
            }
        }
    };

    private static AppVersion V(int major, int minor, int patch) => new(major, minor, patch);

    [Fact]
    public void TakesEverythingBetweenTheInstalledVersionAndTheTarget()
    {
        var releases = new[]
        {
            Release("0.0.8"),   // выше цели: пользователь его ещё не ставил
            Release("0.0.7"),   // цель
            Release("0.0.6"),
            Release("0.0.2"),
            Release("0.0.1"),   // установленная: её показывать не нужно
            Release("0.0.0")    // старее установленной
        };

        var changelog = GithubUpdateService.BuildChangelog(releases, V(0, 0, 1), V(0, 0, 7));

        // От новых к старым, без установленной версии и без того, что выше цели.
        Assert.Equal(["0.0.7", "0.0.6", "0.0.2"], changelog.Select(release => release.Version.ToString()));
    }

    [Fact]
    public void SkipsDraftsPrereleasesAndTagsThatAreNotVersions()
    {
        var releases = new[]
        {
            Release("0.0.7"),
            Release("0.0.6-beta.1", prerelease: true),
            Release("0.0.6", draft: true),
            Release("nightly-build"),
            Release("0.0.5")
        };

        var changelog = GithubUpdateService.BuildChangelog(releases, V(0, 0, 4), V(0, 0, 7));

        Assert.Equal(["0.0.7", "0.0.5"], changelog.Select(release => release.TagName));
    }

    /// <summary>
    /// Один тег иногда публикуют дважды (пересборка релиза). Пользователю нужна одна
    /// запись такой версии: иначе описание продублируется.
    /// </summary>
    [Fact]
    public void KeepsOneEntryPerVersion()
    {
        var releases = new[]
        {
            Release("0.0.7", "первая публикация"),
            Release("v0.0.7", "пересборка"),
            Release("0.0.6")
        };

        var changelog = GithubUpdateService.BuildChangelog(releases, V(0, 0, 5), V(0, 0, 7));

        Assert.Equal(2, changelog.Count);
        Assert.Equal("первая публикация", changelog[0].ReleaseNotes);
    }

    /// <summary>
    /// Порядок ответа GitHub — по дате публикации, а читать changelog хочется по
    /// версии: иначе старый бэкпорт, выпущенный позже, встал бы на место цели.
    /// </summary>
    [Fact]
    public void OrdersByVersionNotByPublicationDate()
    {
        var backport = Release("0.0.5");
        backport.PublishedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var releases = new[] { backport, Release("0.0.7"), Release("0.0.6") };

        var changelog = GithubUpdateService.BuildChangelog(releases, V(0, 0, 4), V(0, 0, 7));

        Assert.Equal(["0.0.7", "0.0.6", "0.0.5"], changelog.Select(release => release.Version.ToString()));
    }

    [Fact]
    public async Task PutsEveryMissedReleaseIntoTheUpdate()
    {
        var check = Service(uri => IsLatest(uri)
            ? Json(Release("0.0.7"))
            : Json(new[] { Release("0.0.7"), Release("0.0.6"), Release("0.0.5") }));

        var result = await check.CheckAsync();

        Assert.True(result.UpdateAvailable);
        var update = result.Update!;
        Assert.Equal(["0.0.7", "0.0.6", "0.0.5"],
            update.Changelog.Select(release => release.Version.ToString()));
        Assert.Equal(2, update.SkippedReleases);

        // Целевой релиз остаётся и в полях обновления: по ним работают установка,
        // план подмены файлов и ссылка «открыть в GitHub».
        Assert.Equal("0.0.7", update.Version.ToString());
        Assert.Equal("0.0.7", update.TagName);
        Assert.True(update.CanInstall);
    }

    /// <summary>
    /// Список релизов может не прийти или не разобраться — тогда пользователь всё
    /// равно должен увидеть описание целевого релиза, а не отказ проверки.
    /// </summary>
    [Fact]
    public async Task MissingReleaseListDoesNotBreakTheCheck()
    {
        var check = Service(uri => IsLatest(uri)
            ? Json(Release("0.0.7"))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await check.CheckAsync();

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Single(result.Update!.Changelog);
        Assert.Equal("0.0.7", result.Update.Changelog[0].TagName);
        Assert.Equal(0, result.Update.SkippedReleases);
    }

    [Fact]
    public async Task GarbledReleaseListDoesNotBreakTheCheck()
    {
        var check = Service(uri => IsLatest(uri) ? Json(Release("0.0.7")) : Raw("<html>лимит запросов</html>"));

        var result = await check.CheckAsync();

        Assert.True(result.Success);
        Assert.Single(result.Update!.Changelog);
    }

    /// <summary>
    /// Список может не содержать целевой релиз (переименованный тег). Окно обновления
    /// показывает именно changelog, и без цели в нём оно опустело бы.
    /// </summary>
    [Fact]
    public async Task TargetReleaseIsAlwaysInTheChangelog()
    {
        var check = Service(uri => IsLatest(uri)
            ? Json(Release("0.0.7"))
            : Json(new[] { Release("0.0.6") }));

        var result = await check.CheckAsync();

        Assert.Equal(["0.0.7", "0.0.6"],
            result.Update!.Changelog.Select(release => release.Version.ToString()));
    }

    [Fact]
    public async Task UpToDateDoesNotAskForTheReleaseList()
    {
        var handler = new StubHandler(uri => IsLatest(uri)
            ? Json(Release("0.0.1"))
            : Raw("[]"));

        var check = new GithubUpdateService(new HttpClient(handler), V(0, 0, 1));
        var result = await check.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Update);

        // Лишний запрос к API тут только впустую: обновлений нет, changelog не нужен.
        Assert.Single(handler.Requests);
    }

    private static bool IsLatest(Uri uri) => uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal);

    private static HttpResponseMessage Json(object payload) =>
        Raw(JsonSerializer.Serialize(payload));

    private static HttpResponseMessage Raw(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static GithubUpdateService Service(Func<Uri, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { Timeout = TimeSpan.FromSeconds(30) }, V(0, 0, 1));

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }
}