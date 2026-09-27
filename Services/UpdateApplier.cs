using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Применение обновления поверх запущенного приложения.
    ///
    /// Файлы нельзя перезаписать, пока их держит работающий процесс, поэтому подменой
    /// занимается отдельный powershell-процесс: ждёт exit приложения по PID, копирует
    /// распакованный payload в каталог установки и запускает exe заново. Скрипт
    /// генерируется во временный файл и удаляет сам себя после успеха.
    ///
    /// <para><b>Что здесь у hardening (SM-A06).</b></para>
    /// <list type="bullet">
    /// <item>Перед запуском скрипта payload проверяется: наличие и тип PE-файла,
    ///   размер, наличие сборки приложения, подпись Authenticode. Раньше проверялось
    ///   только имя файла, и «повреждённый» архив вполне мог дойти до подмены.</item>
    /// <item>Удаление лишних файлов больше не делегируется <c>robocopy /MIR</c>.
    ///   Список файлов вычисляется заранее, показывается пользователю и удаляется
    ///   скриптом поимённо: /MIR стирает всё, чего нет в архиве, и жертвой может
    ///   стать файл, положенный рядом пользователем.</item>
    /// <item>Скрипт не трогает реестр и каталог данных приложения: ни одной
    ///   операции с реестром и ни одного пути вне каталога установки.</item>
    /// </list>
    /// </summary>
    internal static class UpdateApplier
    {
        /// <summary>Имя главного файла, который обязан быть в распакованном архиве.</summary>
        private const string MainExecutableName = "SoundMeeter.exe";

        /// <summary>Управляющая сборка приложения — без неё архив не является сборкой.</summary>
        private const string MainAssemblyName = "SoundMeeter.dll";

        /// <summary>Минимальный правдоподобный размер exe/сборки: ниже этого это не PE-файл.</summary>
        private const long MinimumFileSize = 16 * 1024;

        /// <summary>
        /// Каталоги и файлы, которые нельзя удалять даже внутри каталога установки.
        /// Это данные приложения и каталоги обновлений: установка в профиль — редкий,
        /// но возможный сценарий, и стирать собственные настройки там нельзя.
        /// </summary>
        private static readonly string[] ProtectedNames = { "logs", "updates", "presets" };

        private static readonly string[] ProtectedFiles =
            { "settings.json", "settings.corrupt.json", "settings.json.tmp", "system_apps_filter.json" };

        private static readonly ILogger Logger = AppLog.For("UpdateApplier");

        /// <summary>
        /// Проверяет целостность распакованной сборки и вычисляет точный список
        /// файлов, которые будут удалены. Бросает исключение, если архив не похож
        /// на сборку SoundMeeter: удалять что-либо в этом случае нельзя.
        /// </summary>
        public static UpdatePlan Plan(string payloadDirectory, string installDirectory, string executablePath,
            string updateRoot, UpdateInfo update)
        {
            if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
                throw new InvalidOperationException("Распакованный архив обновления не найден.");

            var notes = new List<string>();

            // 1) Главный исполняемый файл: есть, непустой, настоящий PE.
            var expectedExecutable = Path.Combine(payloadDirectory, Path.GetFileName(executablePath));
            var mainExe = File.Exists(expectedExecutable)
                ? expectedExecutable
                : Directory.EnumerateFiles(payloadDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), MainExecutableName,
                        StringComparison.OrdinalIgnoreCase));

            if (mainExe == null)
                throw new InvalidOperationException(
                    $"В архиве релиза {update.TagName} нет {MainExecutableName} — обновление не похоже на сборку SoundMeeter.");

            VerifyPortableExecutable(mainExe, notes);

            // 2) Управляющая сборка приложения: без неё запуститься нечем.
            var mainAssembly = Directory.EnumerateFiles(payloadDirectory, MainAssemblyName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (mainAssembly == null)
                throw new InvalidOperationException(
                    $"В архиве релиза {update.TagName} нет {MainAssemblyName} — обновление неполное.");
            if (new FileInfo(mainAssembly).Length < MinimumFileSize)
                throw new InvalidOperationException(
                    $"{MainAssemblyName} в архиве релиза {update.TagName} подозрительно мал " +
                    $"({new FileInfo(mainAssembly).Length} байт) — обновление отклонено.");

            // 3) Подпись Authenticode. Сборки не подписаны, поэтому её отсутствие —
            //    предупреждение; невалидная подпись — уже повод остановиться.
            VerifySignature(mainExe, update, notes);

            var payloadFiles = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories).ToList();
            var newTotalBytes = payloadFiles.Sum(f => SafeLength(f));
            notes.Add($"Файлов в сборке: {payloadFiles.Count}, суммарно {FormatBytes(newTotalBytes)}");

            var stale = CollectFilesToDelete(payloadDirectory, installDirectory);
            notes.Add(stale.Count == 0
                ? "Устаревших файлов в каталоге установки нет"
                : $"Будет удалено файлов: {stale.Count}");

            Logger.LogInformation("План обновления {Tag}: {Notes}", update.TagName, string.Join("; ", notes));
            Logger.LogInformation("Каталог установки: {Install}, запускаемый файл: {Exe}", installDirectory, executablePath);
            foreach (var path in stale)
                Logger.LogWarning("Будет удалён устаревший файл: {Path}", path);

            return new UpdatePlan
            {
                PayloadDirectory = payloadDirectory,
                InstallDirectory = installDirectory,
                ExecutablePath = executablePath,
                FilesToDelete = stale,
                NewFileCount = payloadFiles.Count,
                NewTotalBytes = newTotalBytes,
                ValidationNotes = notes,
                TagName = update.TagName
            };
        }

        /// <summary>Минимальная проверка PE-файла: сигнатура MZ и разумный размер.</summary>
        private static void VerifyPortableExecutable(string path, List<string> notes)
        {
            var info = new FileInfo(path);
            if (info.Length < MinimumFileSize)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} в архиве релиза подозрительно мал ({info.Length} байт) — " +
                    "это не исполняемый файл Windows.");

            using var stream = File.OpenRead(path);
            var header = new byte[2];
            if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} не является исполняемым файлом Windows (нет сигнатуры MZ).");

            notes.Add($"{Path.GetFileName(path)}: PE-заголовок в порядке, {FormatBytes(info.Length)}");
        }

        private static void VerifySignature(string executable, UpdateInfo update, List<string> notes)
        {
            var (state, subject, detail) = AuthenticodeVerifier.Verify(executable);
            switch (state)
            {
                case SignatureState.Valid:
                    notes.Add($"Подпись Authenticode действительна: {subject}");
                    Logger.LogInformation("Подпись Authenticode {Exe} действительна: {Subject}", executable, subject);
                    break;
                case SignatureState.Invalid:
                    Logger.LogError("Подпись Authenticode {Exe} не проходит проверку: {Detail}", executable, detail);
                    throw new InvalidOperationException(
                        $"Подпись Authenticode файла {Path.GetFileName(executable)} недействительна " +
                        $"({detail}) — обновление отклонено.");
                default:
                    notes.Add($"{Path.GetFileName(executable)} не подписан (сборки SoundMeeter без сертификата)");
                    Logger.LogWarning(
                        "{Exe}: {Detail}; целостность подтверждена только структурой архива. Тег: {Tag}",
                        executable, detail, update.TagName);
                    break;
            }
        }

        /// <summary>
        /// Файлы каталога установки, которых нет в новой сборке. Сравнение имён
        /// регистронезависимое (как в Windows), пути нормализуются относительно
        /// payload. Каталоги приложения (настройки, логи, обновления) исключаются явно.
        /// </summary>
        private static List<string> CollectFilesToDelete(string payloadDirectory, string installDirectory)
        {
            var result = new List<string>();
            if (!Directory.Exists(installDirectory)) return result;

            var payload = new HashSet<string>(
                Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                    .Select(f => RelativePath(payloadDirectory, f)),
                StringComparer.OrdinalIgnoreCase);

            string installPrefix = installDirectory.TrimEnd('\\') + "\\";

            foreach (var file in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
            {
                if (payload.Contains(RelativePath(payloadDirectory, file))) continue;
                if (IsProtected(file, installPrefix)) continue;
                result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool IsProtected(string file, string installPrefix)
        {
            // Данные приложения лежат вне каталога установки и в силу этого
            // недостижимы для перечисления; защищаем тот случай, когда каталог
            // установки совпал с каталогом данных (portable-установка в профиле).
            if (ProtectedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) return true;

            var relative = file.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase)
                ? file[installPrefix.Length..]
                : file;

            var segments = relative.Split('\\', '/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (ProtectedNames.Contains(segments[i], StringComparer.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string RelativePath(string root, string file)
        {
            var prefix = root.TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(file);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full[prefix.Length..]
                : Path.GetFileName(full);
        }

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; }
            catch { return 0; }
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
        }

        /// <summary>
        /// Запускает фоновый скрипт подмены. Список удаляемых файлов передаётся
        /// скрипту явно и удаляется поимённо — никакого «удали всё, чего нет в архиве».
        /// </summary>
        public static void ApplyAndRestart(UpdatePlan plan)
        {
            EnsureWritable(plan.InstallDirectory);

            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"SoundMeeter-update-{Guid.NewGuid():N}.ps1");
            var script = BuildScript(
                Environment.ProcessId,
                plan,
                Path.Combine(Path.GetTempPath(),
                    $"SoundMeeter-update-{Guid.NewGuid():N}.trash"),
                scriptPath);

            // UTF-8 С BOM обязателен: Windows PowerShell 5.1 читает .ps1 без BOM как ANSI,
            // и кириллица в комментариях ломает парсер скрипта.
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            Process.Start(startInfo)?.Dispose();

            Logger.LogInformation(
                "Обновление {Tag} передано фоновому скрипту {Script}: копирование {Payload} -> {Install}, " +
                "удаление {Deletes} файл(ов), перезапуск {Exe}",
                plan.TagName, scriptPath, plan.PayloadDirectory, plan.InstallDirectory,
                plan.FilesToDelete.Count, plan.ExecutablePath);
        }

        private static void EnsureWritable(string directory)
        {
            var probe = Path.Combine(directory, $".sm-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new UnauthorizedAccessException(
                    $"Нет прав на запись в папку установки «{directory}». " +
                    "Запустите SoundMeeter от имени администратора.", ex);
            }
        }

        /// <summary>
        /// Шаблон фонового скрипта. Плейсхолдеры вместо интерполяции C#: в теле
        /// полно фигурных скобок PowerShell, которые конфликтуют с интерполяцией.
        /// </summary>
        private const string ApplyScriptTemplate = @"
# Сгенерировано SoundMeeter. Не редактировать: файл удаляется после запуска.
# Ждёт выхода приложения, подменяет файлы сборки и перезапускает его.
# Список удаляемых файлов передан из приложения и уже показан пользователю:
# скрипт НЕ применяет /MIR и не удаляет ничего сверх этого списка.

$ErrorActionPreference = 'Continue'
$log = '@LOG@'
$appPid = @PID@
$src = '@SRC@'
$dst = '@DST@'
$exe = '@EXE@'
$trash = '@TRASH@'
$stale = @STALE@

function Fail([string]$message) {
    $message | Out-File -LiteralPath $log -Append -Encoding utf8
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show($message, 'SoundMeeter: обновление') | Out-Null
    } catch { }
    exit 1
}

function Note([string]$message) {
    ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message) |
        Out-File -LiteralPath $log -Append -Encoding utf8
}

Note ('start, src=' + $src + ' dst=' + $dst)

# Ждём завершения процесса приложения (максимум 5 минут).
# В powershell $PID — автоматическая read-only переменная, поэтому своё имя: $appPid.
for ($i = 0; $i -lt 600; $i++) {
    if (-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
}
if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) {
    Fail 'Приложение не завершилось за 5 минут — обновление отменено.'
}
Start-Sleep -Milliseconds 700

# Файлы могут быть ещё заняты антивирусом/поиском: до 20 попыток.
# Копируем БЕЗ /MIR: robocopy /MIR удалил бы всё, чего нет в архиве, — а это
# в том числе файлы, которые положил рядом пользователь.
$copied = $false
for ($i = 0; $i -lt 20; $i++) {
    & robocopy.exe $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -lt 8) { $copied = $true; break }
    Start-Sleep -Milliseconds 700
}
if (-not $copied) {
    Fail ('Не удалось заменить файлы в ' + $dst + '. Закройте программы, использующие файлы SoundMeeter, и повторите обновление.')
}
Note 'copy done'

# Удаляем ровно те файлы, которые показаны пользователю и перечислены в $stale.
$failed = 0
foreach ($path in $stale) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    try {
        Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        Note ('deleted ' + $path)
    } catch {
        $failed++
        Note ('NOT deleted ' + $path + ' : ' + $_.Exception.Message)
    }
}
if ($failed -gt 0) {
    Note ('warning: ' + $failed + ' file(s) left in place; they do not block the update')
}

# Payload отработал — убираем за собой мусор и скрипт ДО перезапуска,
# чтобы не оставлять мусор даже если запуск не удался.
if (Test-Path -LiteralPath $trash) {
    Remove-Item -LiteralPath $trash -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue

try {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
} catch {
    Fail ('Файлы обновлены, но запустить приложение не удалось. Откройте SoundMeeter.exe вручную: ' + $exe)
}
";

        private static string BuildScript(int processId, UpdatePlan plan, string trashPath, string scriptPath)
        {
            var stale = plan.FilesToDelete.Count == 0
                ? "@()"
                : "@(" + string.Join(", ", plan.FilesToDelete.Select(p => "'" + Escape(p) + "'")) + ")";

            return ApplyScriptTemplate
                .Replace("@LOG@", Escape(scriptPath) + ".log")
                .Replace("@PID@", processId.ToString(CultureInfo.InvariantCulture))
                .Replace("@SRC@", Escape(plan.PayloadDirectory))
                .Replace("@DST@", Escape(plan.InstallDirectory))
                .Replace("@EXE@", Escape(plan.ExecutablePath))
                .Replace("@TRASH@", Escape(trashPath))
                .Replace("@STALE@", stale);
        }

        private static string Escape(string value) => value.Replace("'", "''");

        /// <summary>
        /// Текст списка удаляемых файлов для диалога подтверждения.
        /// </summary>
        public static string FormatDeletionList(IReadOnlyList<string> files)
        {
            if (files.Count == 0) return "Устаревших файлов в каталоге установки не найдено.";

            var sb = new StringBuilder();
            sb.Append(files.Count == 1 ? "Будет удалён 1 файл:" : $"Будет удалено файлов: {files.Count}");

            int shown = 0;
            foreach (var file in files)
            {
                if (shown == 50)
                {
                    sb.Append($"\n… и ещё {files.Count - shown} файлов (полный список в журнале)");
                    break;
                }
                sb.Append("\n  • ").Append(file);
                shown++;
            }

            return sb.ToString();
        }
    }
}
