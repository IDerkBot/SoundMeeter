using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services;

/// <summary>
/// Реализация <see cref="IStartupService"/> поверх планировщика задач Windows.
/// Ключ и имя значения из <c>HKCU\...\Run</c> вынесены в константы: там может
/// остаться запись предыдущих сборок, и её нужно находить и удалять.
/// </summary>
public sealed class StartupService : IStartupService
{
    /// <summary>Ветка, в которой автозапуск жил до перехода на задачу планировщика.</summary>
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal const string ValueName = "SoundMeeter";

    private readonly ILogger _logger = AppLog.For<StartupService>();

    /// <inheritdoc />
    public bool IsEnabled => ReadState(migrateLegacyEntry: true);

    /// <inheritdoc />
    public bool SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string? executable = ExecutablePath();
            if (executable is null)
            {
                _logger.LogWarning(
                    "Автозапуск не включён: не найден исполняемый файл приложения");
                return ReadState(migrateLegacyEntry: false);
            }

            if (TaskSchedulerClient.TryCreateOrUpdate(executable))
            {
                RemoveLegacyEntry();
                _logger.LogInformation("Автозапуск включён: {Path}", executable);
            }
        }
        else
        {
            RemoveLegacyEntry();
            TaskSchedulerClient.Delete();
        }

        // Миграция при чтении состояния здесь была бы вредной: после неудачного
        // выключения она вернула бы задачу, которую только что удалили.
        return ReadState(migrateLegacyEntry: false);
    }

    /// <summary>
    /// Что система считает включённым. Задача — источник истины; запись в ключе
    /// <c>Run</c> тоже означает включённый автозапуск, поэтому на время перехода
    /// она учитывается и по возможности переносится в задачу (см.
    /// <see cref="IStartupService.IsEnabled"/>).
    /// </summary>
    private bool ReadState(bool migrateLegacyEntry)
    {
        if (TaskSchedulerClient.Exists()) return true;
        if (!HasLegacyEntry()) return false;

        string? executable = ExecutablePath();
        if (migrateLegacyEntry &&
            executable is not null &&
            TaskSchedulerClient.TryCreateOrUpdate(executable))
        {
            RemoveLegacyEntry();
        }

        return true;
    }

    private bool HasLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать состояние автозапуска: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Убирает запись из ключа <c>Run</c>: пока она есть, автозапуск отработает
    /// ещё и с запросом UAC, то есть ровно то, ради чего задача и заводилась.
    /// </summary>
    private void RemoveLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Запись автозапуска из ключа Run удалена");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить запись из ключа Run: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Полный путь к запускаемому файлу. <see cref="Environment.ProcessPath"/>
    /// указывает на реальный исполняемый образ, а
    /// <see cref="System.Reflection.AssemblyName.CodeBase"/> в .NET Core может
    /// указывать вовсе не туда — поэтому берём именно ProcessPath.
    /// </summary>
    private static string? ExecutablePath()
    {
        string? path = Environment.ProcessPath;

        // Сборка запущена через `dotnet SoundMeeter.dll` (или тестом) — записывать
        // такой путь в автозапуск бессмысленно: Windows не запустит его как
        // приложение, и запись пришлось бы каждый раз удалять вручную.
        if (string.IsNullOrWhiteSpace(path) ||
            path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("testhost.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        return path;
    }
}
