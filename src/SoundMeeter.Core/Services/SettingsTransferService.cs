using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Security;
using System.Text.Json;

namespace SoundMeeter.Services
{
    /// <summary>Итог экспорта настроек в файл.</summary>
    public sealed record SettingsExportResult(bool Success, string Message);

    /// <summary>
    /// Итог чтения файла настроек при импорте. <see cref="Settings"/> заполнен
    /// только при успехе, и он уже прогнан через <see cref="SettingsMigrator"/> —
    /// применять его можно сразу. <see cref="BackupPath"/> появляется после
    /// применения: это путь к копии настроек, которые импорт заменил.
    /// </summary>
    public sealed record SettingsImportResult(bool Success, AppSettings? Settings, string Message,
        string BackupPath = "")
    {
        public static SettingsImportResult Failed(string message) => new(false, null, message);

        public static SettingsImportResult Read(AppSettings settings, string message) =>
            new(true, settings, message);
    }

    /// <summary>
    /// Импорт и экспорт всех настроек одним файлом (SM-C09).
    ///
    /// Формат файла — тот же <see cref="AppSettings"/>, что и у settings.json:
    /// экспорт это снимок того, что приложение пишет на диск, а импорт — обратная
    /// операция. Отдельный формат с версией и манифестом здесь был бы лишним:
    /// что ещё должно попасть в переносимые настройки, решает состав полей
    /// <see cref="AppSettings"/>, и он меняется только вместе с версией схемы.
    ///
    /// Служба ничего не применяет: она умеет только записать и прочитать файл.
    /// Применение — за <see cref="ViewModels.MainViewModel"/>, потому что оно
    /// трогает и движок, и Loc, и AppLog, и состояние системы (автозапуск).
    /// </summary>
    public class SettingsTransferService
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private readonly SettingsService _settings;
        private readonly ILogger _logger = AppLog.For<SettingsTransferService>();

        public SettingsTransferService(SettingsService settings) => _settings = settings;

        /// <summary>Фильтр диалогов файлов: тот же JSON, что у settings.json.</summary>
        public const string FileFilter = "SoundMeeter settings (*.json)|*.json|All files (*.*)|*.*";

        /// <summary>Имя файла по умолчанию, например SoundMeeter-2026-10-04.json.</summary>
        public static string SuggestFileName(string suffix)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd");
            return $"SoundMeeter-{(suffix.Length > 0 ? suffix + "-" : string.Empty)}{stamp}.json";
        }

        /// <summary>
        /// Пишет все настройки в <paramref name="path"/>. Ошибка возвращается в
        /// <see cref="SettingsExportResult"/>: диалог сохранения уже закрылся, и
        /// бросать исключение в обработчике клика означало бы уронить приложение
        /// из-за неудачного имени файла.
        /// </summary>
        public SettingsExportResult Export(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new(false, Loc.Get("Sm.Transfer.NoPath"));

            try
            {
                var snapshot = _settings.CreateFullSnapshot();
                var json = JsonSerializer.Serialize(snapshot, Options);

                var full = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(full, json);
                _logger.LogInformation("Настройки экспортированы в {Path}", full);

                return new(true, Loc.Get("Sm.Transfer.Exported", Path.GetFileName(full)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException
                                          or JsonException or SecurityException)
            {
                _logger.LogError(ex, "Экспорт настроек не удался: {Message}", ex.Message);
                return new(false, Loc.Get("Sm.Transfer.Failed", ex.Message));
            }
        }

        /// <summary>
        /// Читает файл настроек и готовит его к применению: разбор, проверка версии
        /// схемы и миграции. Ничего не применяется — это работа вызывающего.
        /// </summary>
        public SettingsImportResult Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return SettingsImportResult.Failed(Loc.Get("Sm.Transfer.NoPath"));

            if (!File.Exists(path))
                return SettingsImportResult.Failed(
                    Loc.Get("Sm.Transfer.NotFound", Path.GetFileName(path)));

            AppSettings? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or JsonException or ArgumentException or NotSupportedException)
            {
                // Файл выбран пользователем, а не наш: отказ разбирать его не должен
                // выглядеть как поломка приложения.
                _logger.LogWarning(ex, "Файл настроек {Path} не читается: {Message}", path, ex.Message);
                return SettingsImportResult.Failed(Loc.Get("Sm.Transfer.Unreadable", ex.Message));
            }

            if (loaded == null)
                return SettingsImportResult.Failed(
                    Loc.Get("Sm.Transfer.Unreadable", "empty document"));

            var outcome = SettingsMigrator.Migrate(loaded, out var note);

            if (outcome == MigrationOutcome.UnsupportedNewerVersion)
                return SettingsImportResult.Failed(Loc.Get("Sm.Transfer.NewerSchema",
                    loaded.SchemaVersion, SettingsMigrator.CurrentSchemaVersion));

            // Чужой JSON разбирается в AppSettings без ошибок и молча даёт пустой
            // документ: импорт такого снёс бы микшер до нуля стрипов. Настоящий
            // файл настроек полосы всегда содержит — их создаёт EnsureDefaultStrips
            // даже на пустом старте, — поэтому пустой результат читаем как чужой файл.
            if (loaded.Inputs is null || loaded.Outputs is null
                || loaded.Inputs.Count + loaded.Outputs.Count == 0)
            {
                return SettingsImportResult.Failed(Loc.Get("Sm.Transfer.NoStrips"));
            }

            if (note.Length > 0)
                _logger.LogInformation("Импорт настроек из {Path}: {Note}", path, note);

            return SettingsImportResult.Read(loaded, note);
        }
    }
}