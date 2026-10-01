using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Проверка обновлений по GitHub Releases: GET api.github.com/repos/{owner}/{repo}/releases/latest.
    /// endpoint /releases/latest сам не отдаёт черновики и пре-релизы, поэтому достаточно
    /// одного запроса. Дальше — выбор portable-zip ассета, скачивание с прогрессом,
    /// распаковка и подмена файлов (см. <see cref="UpdateApplier"/>).
    ///
    /// Имя класса уточняет ИСТОЧНИК, а не механизм: релизов может быть несколько
    /// (свой сервер, зеркало, корпоративный GitLab), и все они реализуют один и тот
    /// же <see cref="IUpdateService"/>. Поэтому контракт для остального приложения
    /// остаётся <see cref="IUpdateService"/>, а конкретный источник добавляется
    /// рядом — без правок в UI и в диспетчере обновлений.
    /// </summary>
    public class GithubUpdateService : IUpdateService, IDisposable
    {
        public const string Owner = "IDerkBot";
        public const string Repo = "SoundMeeter";

        private const string ApiBase = "https://api.github.com/repos/";

        /// <summary>
        /// Скачивание большого архива упирается в HttpClient.Timeout: таймер живёт до
        /// конца чтения тела ответа, даже при ResponseHeadersRead.
        /// </summary>
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(10);

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _http;
        private readonly ILogger _logger = AppLog.For<GithubUpdateService>();
        private bool _disposed;

        public GithubUpdateService() : this(new HttpClient()) { }

        /// <summary>Конструктор с внешним HttpClient — чтобы подменить в тестах.</summary>
        public GithubUpdateService(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (_http.Timeout == TimeSpan.FromSeconds(100))
            {
                _http.Timeout = HttpTimeout;
            }

            // GitHub отклоняет запросы без User-Agent.
            if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _http.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("SoundMeeter", CurrentVersion.ToString()));
            }

            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public AppVersion CurrentVersion { get; } = AppVersion.Current;

        public string UpdateRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundMeeter",
            "updates");

        public string ExecutablePath =>
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SoundMeeter.exe");

        public string InstallDirectory =>
            Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Проверка обновлений: {Owner}/{Repo}, текущая версия {Version}",
                Owner, Repo, CurrentVersion);
            try
            {
                using var response = await _http
                    .GetAsync($"{ApiBase}{Owner}/{Repo}/releases/latest", cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // 404 — релизов ещё нет. Это не поломка, просто обновляться не от чего.
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _logger.LogInformation("Релизов в {Owner}/{Repo} пока нет", Owner, Repo);
                        return new UpdateCheckResult(true, false, null,
                            Loc.Get("Sm.Update.Check.NoReleases", CurrentVersion, Owner, Repo));
                    }

                    // 403/429 — лимит запросов к API, к самому приложению отношения не имеет.
                    _logger.LogWarning("GitHub API вернул {Status}", (int)response.StatusCode);
                    return UpdateCheckResult.Failed(
                        Loc.Get("Sm.Update.Check.GitHubError", (int)response.StatusCode, response.ReasonPhrase));
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);

                if (dto?.TagName is null || !AppVersion.TryParse(dto.TagName, out var version))
                {
                    _logger.LogWarning("Тег релиза не разобран: {Tag}", dto?.TagName);
                    return UpdateCheckResult.Failed(Loc.Get("Sm.Update.Check.TagUnparsed"));
                }

                var asset = SelectAsset(dto.Assets);
                var update = new UpdateInfo
                {
                    Version = version,
                    TagName = dto.TagName,
                    Title = string.IsNullOrWhiteSpace(dto.Name) ? dto.TagName : dto.Name,
                    ReleaseNotes = dto.Body ?? "",
                    HtmlUrl = dto.HtmlUrl ?? $"https://github.com/{Owner}/{Repo}/releases/tag/{dto.TagName}",
                    PublishedAt = dto.PublishedAt,
                    Asset = asset
                };

                if (version <= CurrentVersion)
                {
                    _logger.LogInformation("Установленная версия {Version} актуальна (последний релиз {Tag})",
                        CurrentVersion, dto.TagName);
                    return new UpdateCheckResult(true, false, null,
                        Loc.Get("Sm.Update.Check.UpToDate", CurrentVersion));
                }

                _logger.LogInformation("Доступно обновление {Version} (установлено {Current}), ассет: {Asset}",
                    version, CurrentVersion, asset?.Name ?? "нет");
                return new UpdateCheckResult(true, true, update,
                    asset == null
                        ? Loc.Get("Sm.Update.Check.AvailableNoPortable", version)
                        : Loc.Get("Sm.Update.Check.AvailableWith", version, asset.Name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Проверка обновлений отменена");
                return UpdateCheckResult.Failed(Loc.Get("Sm.Update.Check.Cancelled"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Проверка обновлений не удалась: {Message}", ex.Message);
                return UpdateCheckResult.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Выбирает архив для автоустановки: обычный .zip, причём предпочтительно
        /// win-x64/win-x86 сборка. Отбрасываем символьные пакеты и исходники.
        /// </summary>
        internal static UpdateAsset? SelectAsset(IEnumerable<GitHubAssetDto> assets)
        {
            UpdateAsset? fallback = null;

            foreach (var dto in assets)
            {
                var name = dto.Name ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("symbols", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("source", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;

                var browserUrl = dto.BrowserDownloadUrl;
                if (string.IsNullOrWhiteSpace(browserUrl)) continue;

                var candidate = new UpdateAsset { Name = name, DownloadUrl = browserUrl, Size = dto.Size };
                fallback ??= candidate;

                if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("win-x86", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("win64", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("win", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return fallback;
        }

        public async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(asset);

            Directory.CreateDirectory(UpdateRoot);
            var target = Path.Combine(UpdateRoot, SafeFileName(asset.Name) + ".zip");
            _logger.LogInformation("Скачивание обновления: {Name} ({Size} байт) -> {Target}",
                asset.Name, asset.Size, target);

            using var response = await _http
                .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var expected = response.Content.Headers.ContentLength ?? asset.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write,
                             FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (expected > 0) progress?.Report(Math.Clamp((double)written / expected, 0, 1));
                }

                // Обрыв связи на середине даёт «успешно» распакованный мусор без
                // этой проверки: размер не совпал — файл неполный, применять нельзя.
                if (expected > 0 && written != expected)
                    throw new InvalidDataException(
                        Loc.Get("Sm.Update.Check.Truncated", written, expected));
            }

            _logger.LogInformation("Архив скачан: {Target} ({Size} байт)", target, new FileInfo(target).Length);
            return target;
        }

        /// <summary>
        /// Распаковывает архив в подкаталог версии. Публикация dotnet нередко кладёт всё
        /// в единственную корневую папку, поэтому спускаемся внутрь неё.
        /// </summary>
        public string Extract(string zipPath, string versionTag)
        {
            var target = Path.Combine(UpdateRoot, SafeFileName(versionTag));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            ZipFile.ExtractToDirectory(zipPath, target);
            var payload = FindPayloadRoot(target);
            _logger.LogInformation("Архив распакован: {Zip} -> {Payload}", zipPath, payload);
            return payload;
        }

        public UpdatePlan PlanUpdate(string payloadDirectory, UpdateInfo update) =>
            UpdateApplier.Plan(payloadDirectory, InstallDirectory, ExecutablePath, UpdateRoot, update);

        public void ApplyAndRestart(UpdatePlan plan) => UpdateApplier.ApplyAndRestart(plan);

        public string FormatPlanReport(UpdatePlan plan)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Loc.Get("Sm.Update.Report.Header", plan.TagName));
            foreach (var note in plan.ValidationNotes)
                sb.Append("  • ").AppendLine(note);

            sb.AppendLine();
            sb.AppendLine(UpdateApplier.FormatDeletionList(plan.FilesToDelete));
            sb.AppendLine();
            sb.Append(Loc.Get("Sm.Update.Report.Directory")).AppendLine(plan.InstallDirectory);
            sb.Append(Loc.Get("Sm.Update.Report.Kept"));
            return sb.ToString();
        }

        public void OpenReleasePage(UpdateInfo update)
        {
            if (string.IsNullOrWhiteSpace(update.HtmlUrl)) return;
            _logger.LogInformation("Открываем страницу релиза {Tag}: {Url}", update.TagName, update.HtmlUrl);
            Process.Start(new ProcessStartInfo(update.HtmlUrl) { UseShellExecute = true })?.Dispose();
        }

        internal static string FindPayloadRoot(string extractedDirectory)
        {
            var current = extractedDirectory;
            for (var depth = 0; depth < 3; depth++)
            {
                var entries = Directory.GetFileSystemEntries(current);
                // В корне есть файлы — это и есть payload.
                if (entries.Length == 0 || Array.Exists(entries, File.Exists)) break;
                // Единственная папка внутри — обёртка публикации, спускаемся в неё.
                if (entries.Length != 1) break;

                current = entries[0];
            }

            return current;
        }

        internal static string SafeFileName(string? value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((value ?? "update")
                .Select(c => Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c)
                .ToArray());
            return cleaned.Length == 0 ? "update" : cleaned;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }
    }
}