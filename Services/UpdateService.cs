using SoundMeeter.Models;
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
    /// </summary>
    public class UpdateService : IUpdateService, IDisposable
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
        private bool _disposed;

        public UpdateService() : this(new HttpClient()) { }

        /// <summary>Конструктор с внешним HttpClient — чтобы подменить в тестах.</summary>
        public UpdateService(HttpClient http)
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
                        return new UpdateCheckResult(true, false, null,
                            $"{CurrentVersion} — релизов в {Owner}/{Repo} пока нет");
                    }

                    // 403/429 — лимит запросов к API, к самому приложению отношения не имеет.
                    return UpdateCheckResult.Failed(
                        $"GitHub API вернул {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);

                if (dto?.TagName is null || !AppVersion.TryParse(dto.TagName, out var version))
                    return UpdateCheckResult.Failed("не удалось разобрать тег релиза");

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
                    return new UpdateCheckResult(true, false, null,
                        $"{CurrentVersion} — установленная версия актуальна");
                }

                return new UpdateCheckResult(true, true, update,
                    asset == null
                        ? $"Доступна версия {version}, но без portable-архива"
                        : $"Доступна версия {version} ({asset.Name})");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return UpdateCheckResult.Failed("проверка отменена");
            }
            catch (Exception ex)
            {
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
            }

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
            return FindPayloadRoot(target);
        }

        public void ApplyAndRestart(string payloadDirectory, UpdateInfo update) =>
            UpdateApplier.ApplyAndRestart(payloadDirectory, InstallDirectory, ExecutablePath, UpdateRoot, update);

        public void OpenReleasePage(UpdateInfo update)
        {
            if (string.IsNullOrWhiteSpace(update.HtmlUrl)) return;
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
