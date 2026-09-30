using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Services;

/// <summary>
/// Автозапуск вместе с Windows (SM-D01).
///
/// Способ — ключ <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>:
/// запись в ветку пользователя, поэтому установка не требует прав администратора
/// (в отличие от <c>HKLM</c> и папки «Автозагрузка» в Program Files, куда
/// portable-сборке писать нельзя).
///
/// Ключ <c>Run</c> вместо задачи в планировщике или папки Startup выбран потому,
/// что запись переживает обновление приложения на месте: путь в ней абсолютный,
/// а значит при автообновлении на тот же путь он остаётся верным. Автообновление
/// подставляет новые файлы в каталог установки (SM-A06), поэтому путь не меняется.
/// </summary>
public interface IStartupService
{
    /// <summary>Включён ли автозапуск по факту состояния системы.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Включает или выключает автозапуск. Возвращает итоговое состояние: если
    /// запись в реестр не удалась, возвращается то, что получилось, а не
    /// желаемое — иначе переключатель в интерфейсе показывал бы неправду.
    /// </summary>
    bool SetEnabled(bool enabled);
}

/// <summary>
/// Реализация <see cref="IStartupService"/> поверх реестра. Ключ и имя значения
/// вынесены в константы, чтобы тесты проверяли ровно то место, куда пишет
/// приложение.
/// </summary>
public sealed class StartupService : IStartupService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "SoundMeeter";

    private readonly ILogger _logger = AppLog.For<StartupService>();

    /// <summary>
    /// Текущее состояние ключа. Ключ может отсутствовать (не настроен) — это не
    /// ошибка, а обычное «выключено».
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value))
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось прочитать состояние автозапуска: {Message}", ex.Message);
            }

            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                // Отдельно проверяем, что запускать: без этого в реестр попадёт
                // пустая строка, и автозапуск «включился», но не срабатывал бы.
                string? executable = ExecutablePath();
                if (executable is null)
                {
                    _logger.LogWarning(
                        "Автозапуск не включён: не найден исполняемый файл приложения");
                    return IsEnabled;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                {
                    _logger.LogWarning("Автозапуск не включён: ключ реестра не создан");
                    return IsEnabled;
                }

                key.SetValue(ValueName, $"\"{executable}\"");
                _logger.LogInformation("Автозапуск включён: {Path}", executable);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    _logger.LogInformation("Автозапуск выключен");
                }
            }
        }
        catch (Exception ex)
        {
            // Реестр может оказаться недоступен (политика, отказ профиля).
            // Проверка прав администратора и объяснений пользователю здесь были бы
            // лишними: HKCU доступен обычному приложению почти всегда.
            _logger.LogWarning(ex, "Не удалось изменить автозапуск: {Message}", ex.Message);
        }

        return IsEnabled;
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