using SoundMeeter.Models;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Проверка обновлений по GitHub Releases и их установка поверх текущей копии.
    /// Реализация не бросает наружу исключений при проверке: о сбоях сети сообщает
    /// <see cref="UpdateCheckResult.Success"/>. Исключения возможны уже при скачивании
    /// и применении — их показывает диалог обновления.
    /// </summary>
    public interface IUpdateService
    {
        /// <summary>Версия запущенной сборки.</summary>
        AppVersion CurrentVersion { get; }

        /// <summary>Папка, куда качается и распаковывается обновление.</summary>
        string UpdateRoot { get; }

        /// <summary>Каталог установленного приложения (рядом с exe).</summary>
        string InstallDirectory { get; }

        /// <summary>Путь к запущенному SoundMeeter.exe.</summary>
        string ExecutablePath { get; }

        Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

        /// <summary>Скачивает ассет и возвращает путь к локальному zip.</summary>
        Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>Распаковывает zip и возвращает корень с payload'ом приложения.</summary>
        string Extract(string zipPath, string versionTag);

        /// <summary>
        /// Запущает фоновый скрипт, который дождётся выхода процесса, заменит файлы
        /// в <see cref="InstallDirectory"/> и перезапустит приложение. Возвращает сразу,
        /// дальше вызывающий обязан закрыть приложение.
        /// </summary>
        void ApplyAndRestart(string payloadDirectory, UpdateInfo update);

        /// <summary>Открывает страницу релиза в браузере.</summary>
        void OpenReleasePage(UpdateInfo update);
    }
}
