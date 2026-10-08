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
        /// Нижняя граница для exe, в котором сборка спрятана целиком (single-file).
        /// Отдельная управляющая сборка — сотни килобайт, бандл с рантаймом —
        /// десятки мегабайт; 8 МБ разделяют их с большим запасом и не дают принять
        /// за бандл огрызок или чужой архив.
        /// </summary>
        private const long SingleFileMinimumSize = 8 * 1024 * 1024;

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
                throw new InvalidOperationException(Loc.Get("Sm.Update.Plan.PayloadMissing"));

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
                    Loc.Get("Sm.Update.Plan.NoExe", update.TagName, MainExecutableName));

            VerifyPortableExecutable(mainExe, notes);

            // 2) Управляющая сборка приложения. Обычная portable-сборка кладёт её
            //    рядом с exe (SoundMeeter.dll), а сборка одним файлом прячет всё
            //    внутри exe — и тогда её нет чем подтвердить, кроме размера и
            //    состава payload. Без этой проверки каждое обновление на
            //    single-file отвергалось бы как «это не сборка».
            var mainAssembly = Directory.EnumerateFiles(payloadDirectory, MainAssemblyName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (mainAssembly == null)
            {
                long exeSize = new FileInfo(mainExe).Length;

                // Порог отличает бандл от огрызка: exe одной сборки весит сотни
                // килобайт, а self-contained single-file — десятки мегабайт.
                // Всё остальное (частично распакованный архив, мусор) отвергается.
                //
                // Считаем ТОЛЬКО сборочные файлы: рядом с exe законно лежит
                // system_apps_filter.json (его пользователь правит руками), и его
                // наличие не делает архив неполным. А вот чужая dll рядом означает
                // ровно обратное: сборка разъехалась, и запускать нечего.
                var assemblies = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                    .Where(file => IsAssemblyLeftover(Path.GetFileName(file) ?? ""))
                    .ToList();

                if (assemblies.Count > 0 || exeSize < SingleFileMinimumSize)
                    throw new InvalidOperationException(
                        Loc.Get("Sm.Update.Plan.NoAssembly", update.TagName, MainAssemblyName));

                notes.Add(Loc.Get("Sm.Update.Plan.SingleFile", FormatBytes(exeSize)));
                Logger.LogInformation("Сборка одним файлом: {Size}, управляющая сборка внутри exe", exeSize);
            }
            else if (new FileInfo(mainAssembly).Length < MinimumFileSize)
            {
                throw new InvalidOperationException(Loc.Get("Sm.Update.Plan.AssemblyTooSmall",
                    MainAssemblyName, update.TagName, new FileInfo(mainAssembly).Length));
            }

            // 3) Подпись Authenticode. Сборки не подписаны, поэтому её отсутствие —
            //    предупреждение; невалидная подпись — уже повод остановиться.
            VerifySignature(mainExe, update, notes);

            var payloadFiles = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories).ToList();
            var newTotalBytes = payloadFiles.Sum(f => SafeLength(f));
            notes.Add(Loc.Get("Sm.Update.Plan.FilesTotal", payloadFiles.Count, FormatBytes(newTotalBytes)));

            var stale = CollectFilesToDelete(payloadDirectory, installDirectory);
            notes.Add(stale.Count == 0
                ? Loc.Get("Sm.Update.Plan.NoStale")
                : Loc.Get("Sm.Update.Plan.StaleCount", stale.Count));

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
                    Loc.Get("Sm.Update.Plan.ExeTooSmall", Path.GetFileName(path), info.Length));

            using var stream = File.OpenRead(path);
            var header = new byte[2];
            if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidOperationException(
                    Loc.Get("Sm.Update.Plan.NotAnExe", Path.GetFileName(path)));

            notes.Add(Loc.Get("Sm.Update.Plan.PeOk", Path.GetFileName(path), FormatBytes(info.Length)));
        }

        private static void VerifySignature(string executable, UpdateInfo update, List<string> notes)
        {
            var (state, subject, detail) = AuthenticodeVerifier.Verify(executable);
            switch (state)
            {
                case SignatureState.Valid:
                    notes.Add(Loc.Get("Sm.Update.Plan.SignatureValid", subject));
                    Logger.LogInformation("Подпись Authenticode {Exe} действительна: {Subject}", executable, subject);
                    break;
                case SignatureState.Invalid:
                    Logger.LogError("Подпись Authenticode {Exe} не проходит проверку: {Detail}", executable, detail);
                    throw new InvalidOperationException(
                        Loc.Get("Sm.Update.Plan.SignatureInvalid", Path.GetFileName(executable), detail));
                default:
                    notes.Add(Loc.Get("Sm.Update.Plan.Unsigned", Path.GetFileName(executable)));
                    Logger.LogWarning(
                        "{Exe}: {Detail}; целостность подтверждена только структурой архива. Тег: {Tag}",
                        executable, detail, update.TagName);
                    break;
            }
        }

        /// <summary>
        /// Файлы каталога установки, которых нет в новой сборке.
        ///
        /// Удаляется ровно одно: остатки прежней многофайловой сборки — dll и pdb.
        /// Всё остальное не трогаем никогда: ни json и ini, ни папки, ни файлы, которые
        /// положил рядом пользователь. Именно так переход на публикацию одним файлом
        /// вычищает старые сборки, не превращаясь в «удали всё, чего нет в архиве».
        ///
        /// Папки обходятся насквозь, но не удаляются: в них лежат satellite-сборки
        /// локализаций, и оставленная старая `ru\....resources.dll` перебила бы
        /// локализацию, вшитую в новый exe, — интерфейс показал бы строки прошлой
        /// версии (и «⟨Sm.Ключ⟩» на новых ключах). Сама папка остаётся на месте.
        /// </summary>
        private static List<string> CollectFilesToDelete(string payloadDirectory, string installDirectory)
        {
            var result = new List<string>();
            if (!Directory.Exists(installDirectory)) return result;

            var payload = new HashSet<string>(
                Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetFileName(file) ?? ""),
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!IsAssemblyLeftover(name)) continue;
                if (payload.Contains(name)) continue;
                result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Остаток старой сборки: только dll и pdb. Расширение, а не белый список
        /// имён: правило должно пережить и новую сборку, и файлы сторонних
        /// библиотек, и не дать удалить ничего, что не является сборкой.
        /// </summary>
        private static bool IsAssemblyLeftover(string name) =>
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; }
            catch { return 0; }
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units =
            {
                Loc.Get("Sm.Unit.Bytes"),
                Loc.Get("Sm.Unit.Kilobytes"),
                Loc.Get("Sm.Unit.Megabytes"),
                Loc.Get("Sm.Unit.Gigabytes"),
            };
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
        /// Тексты, которые скрипт показывает пользователю в MessageBox, не
        /// захардкожены: они подставляются из ресурсов в <see cref="BuildScript"/>,
        /// иначе после смены языка ошибка обновления осталась бы на старом.
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
$caption = '@CAPTION@'
$msgAppDidNotExit = '@MSGAPPEXIT@'
$msgCopyFailed = '@MSGCOPYFAILED@'
$msgLaunchFailed = '@MSGLAUNCHFAILED@'

function Fail([string]$message) {
    $message | Out-File -LiteralPath $log -Append -Encoding utf8
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show($message, $caption) | Out-Null
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
    Fail $msgAppDidNotExit
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
    Fail ($msgCopyFailed -f $dst)
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
    Fail ($msgLaunchFailed -f $exe)
}
";

        private static string BuildScript(int processId, UpdatePlan plan, string trashPath, string scriptPath)
        {
            var stale = plan.FilesToDelete.Count == 0
                ? "@()"
                : "@(" + string.Join(", ", plan.FilesToDelete.Select(p => "'" + Escape(p) + "'")) + ")";

            return ApplyScriptTemplate
                .Replace("@LOG@", Escape(scriptPath))
                .Replace("@PID@", processId.ToString(CultureInfo.InvariantCulture))
                .Replace("@SRC@", Escape(plan.PayloadDirectory))
                .Replace("@DST@", Escape(plan.InstallDirectory))
                .Replace("@EXE@", Escape(plan.ExecutablePath))
                .Replace("@TRASH@", Escape(trashPath))
                .Replace("@STALE@", stale)
                // Скрипт переживает перезапуск приложения, поэтому тексты в нём
                // фиксируются на момент запуска обновления.
                .Replace("@CAPTION@", Escape(Loc.Get("Sm.Update.Script.Caption")))
                .Replace("@MSGAPPEXIT@", Escape(Loc.Get("Sm.Update.Script.AppDidNotExit")))
                .Replace("@MSGCOPYFAILED@", Escape(Loc.Get("Sm.Update.Script.CopyFailed", "{0}")))
                .Replace("@MSGLAUNCHFAILED@", Escape(Loc.Get("Sm.Update.Script.LaunchFailed", "{0}")));
        }


        private static string Escape(string value) => value.Replace("'", "''");

        /// <summary>
        /// Текст списка удаляемых файлов для диалога подтверждения.
        /// </summary>
        public static string FormatDeletionList(IReadOnlyList<string> files)
        {
            if (files.Count == 0) return Loc.Get("Sm.Update.Plan.DeletionNone");

            var sb = new StringBuilder();
            sb.Append(files.Count == 1
                ? Loc.Get("Sm.Update.Plan.DeletionOne")
                : Loc.Get("Sm.Update.Plan.DeletionMany", files.Count));

            int shown = 0;
            foreach (var file in files)
            {
                if (shown == 50)
                {
                    sb.Append("\n").Append(Loc.Get("Sm.Update.Plan.DeletionMore", files.Count - shown));
                    break;
                }
                sb.Append("\n  • ").Append(file);
                shown++;
            }

            return sb.ToString();
        }
    }
}