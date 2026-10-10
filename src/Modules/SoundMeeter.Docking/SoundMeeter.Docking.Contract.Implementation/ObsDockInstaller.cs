using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Docking.Contract;
using SoundMeeter.Docking.Entities;
using System.Diagnostics;

namespace SoundMeeter.Docking.Contract.Implementation;

/// <summary>
/// Обёртка над конфигом OBS: знает, где лежит user.ini, и НЕ ДАЁТ править его,
/// пока запущен OBS. Работа с самим файлом — в <see cref="ObsDockConfig"/>.
///
/// Проверка «OBS закрыт» живёт именно здесь, а не в коде правки, чтобы её нельзя
/// было случайно обойти и затереть файл: работающий OBS переписывает user.ini
/// целиком при выходе, и наша запись пропала бы вместе с чужими настройками.
/// </summary>
public sealed class ObsDockInstaller : IObsDockInstaller
{
    private const string UserIniName = "user.ini";

    /// <summary>Каталог конфигурации OBS. По умолчанию %APPDATA%\obs-studio.</summary>
    public static string DefaultConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    private readonly string _configDirectory;

    public ObsDockInstaller() : this(DefaultConfigDirectory) { }

    /// <summary>Явный каталог — нужен проверкам, чтобы не трогать конфиг пользователя.</summary>
    public ObsDockInstaller(string configDirectory) => _configDirectory = configDirectory;

    /// <summary>Путь к user.ini, который правим (может не существовать — OBS ещё не запускался).</summary>
    public string UserIniPath => Path.Combine(_configDirectory, UserIniName);

    /// <summary>Запущен ли сейчас OBS.</summary>
    public bool IsObsRunning => IsObsRunningImpl();

    /// <summary>
    /// Проверка списка процессов. Отдельный метод, потому что вызывается и из
    /// экземпляра (свойство контракта), и напрямую из <see cref="Guarded"/> —
    /// без установщика тоже должно работать.
    /// </summary>
    public static bool IsObsRunningImpl()
    {
        foreach (var name in new[] { "obs64", "obs32" })
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Есть ли уже наш док в списке (нужно окну настроек для статуса).</summary>
    public bool IsDockInstalled() => ObsDockConfig.IsDockInstalled(UserIniPath);

    /// <summary>Добавляет док (или обновляет его URL). Требует закрытого OBS.</summary>
    public ObsDockInstallResult Install(string url) =>
        Guarded(() => ObsDockConfig.Install(UserIniPath, url));

    /// <summary>Убирает наш док из списка. Требует закрытого OBS.</summary>
    public ObsDockInstallResult Uninstall() =>
        Guarded(() => ObsDockConfig.Uninstall(UserIniPath));

    private static ObsDockInstallResult Guarded(Func<ObsDockInstallResult> action)
    {
        if (IsObsRunningImpl())
        {
            return ObsDockInstallResult.Fail(Loc.Get("Sm.Obs.ObsRunning"));
        }
        return action();
    }
}
