using SoundMeeter.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Применение обновления поверх запущенного приложения.
    ///
    /// Файлы нельзя перезаписать, пока их держит работающий процесс, поэтому подменой
    /// занимается отдельный powershell-процесс: ждёт exit приложения по PID, зеркалит
    /// распакованный payload в каталог установки (robocopy /MIR) и запускает exe заново.
    /// Скрипт генерируется во временный файл и удаляет сам себя после успеха.
    /// </summary>
    internal static class UpdateApplier
    {
        /// <summary>Имя главного файла, который обязан быть в распакованном архиве.</summary>
        private const string MainExecutableName = "SoundMeeter.exe";

        public static void ApplyAndRestart(string payloadDirectory, string installDirectory,
            string executablePath, string updateRoot, UpdateInfo update)
        {
            if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
                throw new InvalidOperationException("Распакованный архив обновления не найден.");

            // Защита от «успешной» подмены всего каталога мусором: без нашего exe
            // распакованный набор файлов обновлением не является.
            var expectedExecutable = Path.Combine(payloadDirectory, Path.GetFileName(executablePath));
            var hasOwnExecutable = File.Exists(expectedExecutable) ||
                Directory.EnumerateFiles(payloadDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                    .Any(f => string.Equals(Path.GetFileName(f), MainExecutableName,
                        StringComparison.OrdinalIgnoreCase));
            if (!hasOwnExecutable)
            {
                throw new InvalidOperationException(
                    $"В архиве релиза {update.TagName} нет {MainExecutableName} — обновление не похоже на сборку SoundMeeter.");
            }

            EnsureWritable(installDirectory);

            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"SoundMeeter-update-{Guid.NewGuid():N}.ps1");
            var script = BuildScript(
                Environment.ProcessId,
                payloadDirectory,
                installDirectory,
                executablePath,
                updateRoot,
                scriptPath);

            // UTF-8 С BOM обязателен: Windows PowerShell 5.1 читает .ps1 без BOM как ANSI,
            // и кириллица в комментариях ломает парсер скрипта.
            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

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

$ErrorActionPreference = 'Continue'
$log = '@LOG@'
$appPid = @PID@
$src = '@SRC@'
$dst = '@DST@'
$exe = '@EXE@'
$trash = '@TRASH@'

function Fail([string]$message) {
    $message | Out-File -LiteralPath $log -Append -Encoding utf8
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show($message, 'SoundMeeter: обновление') | Out-Null
    } catch { }
    exit 1
}

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

# Файлы могут быть ещё заняты антивирусом/поиском: robocopy до 20 попыток.
# Коды 0..7 — успех (0 — ничего не изменилось, 1 — скопировано, 2 — лишние удалены).
$copied = $false
for ($i = 0; $i -lt 20; $i++) {
    & robocopy.exe $src $dst /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -lt 8) { $copied = $true; break }
    Start-Sleep -Milliseconds 700
}
if (-not $copied) {
    Fail ('Не удалось заменить файлы в ' + $dst + '. Закройте программы, использующие файлы SoundMeeter, и повторите обновление.')
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

        private static string BuildScript(int processId, string payloadDirectory, string installDirectory,
            string executablePath, string updateRoot, string scriptPath) =>
            ApplyScriptTemplate
                .Replace("@LOG@", Escape(scriptPath) + ".log")
                .Replace("@PID@", processId.ToString(CultureInfo.InvariantCulture))
                .Replace("@SRC@", Escape(payloadDirectory))
                .Replace("@DST@", Escape(installDirectory))
                .Replace("@EXE@", Escape(executablePath))
                .Replace("@TRASH@", Escape(updateRoot));

        private static string Escape(string value) => value.Replace("'", "''");
    }
}
