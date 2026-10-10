using SoundMeeter.Docking.Entities;

namespace SoundMeeter.Docking.Contract;

/// <summary>
/// Правка конфигурации OBS: добавить или убрать наш док в списке источников.
///
/// Отдельный контракт от <see cref="IObsDockServer"/> не для красоты: сервер живёт
/// в памяти, а установщик трогает файл пользователя и спрашивает у системы,
/// запущен ли OBS. Смешались бы — и проверять пришлось бы с настоящим OBS на
/// машине.
///
/// Проверка «OBS закрыт» лежит в реализации и обойти её нельзя: работающий OBS
/// переписывает user.ini целиком при выходе, и наша запись пропала бы вместе с
/// чужими настройками.
/// </summary>
public interface IObsDockInstaller
{
    /// <summary>Запущен ли сейчас OBS. True — править user.ini нельзя.</summary>
    bool IsObsRunning { get; }

    /// <summary>Есть ли наш док в конфигурации (для статуса в окне настроек).</summary>
    bool IsDockInstalled();

    /// <summary>
    /// Добавляет док или обновляет его URL. Требует закрытого OBS — иначе
    /// возвращает отказ с объяснением.
    /// </summary>
    ObsDockInstallResult Install(string url);

    /// <summary>Убирает наш док из списка. Требует закрытого OBS.</summary>
    ObsDockInstallResult Uninstall();
}