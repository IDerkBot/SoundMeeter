using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Text.Json;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Персистентное хранилище настроек. Реализует <see cref="ISettingsService"/> —
    /// контракт, который ожидают MainViewModel и AudioService (портирован из AudioRouter):
    /// единый снимок настроек в <see cref="Settings"/> + <see cref="Save"/>.
    ///
    /// Два инварианта, оба из SM-A04/SM-A05:
    /// * запись атомарна — временный файл в том же каталоге плюс <see cref="File.Replace"/>,
    ///   поэтому обрыв питания в момент сохранения не оставляет нечитаемый settings.json;
    /// * нечитаемый файл не роняет запуск: он откладывается рядом как
    ///   settings.corrupt.json, а приложение стартует с настройками по умолчанию.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private const string FileName = "settings.json";
        private const string TempFileName = "settings.json.tmp";
        private const string CorruptFileName = "settings.corrupt.json";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        private readonly IAudioEngine _engine;
        private readonly object _writeLock = new();
        private readonly ILogger _logger = AppLog.For<SettingsService>();

        public SettingsService(IAudioEngine engine) => _engine = engine;

        /// <summary>
        /// Конструктор с явным каталогом — для проверок без устройств: иначе
        /// тест писал бы в настоящий %APPDATA% пользователя.
        /// </summary>
        internal SettingsService(IAudioEngine engine, string directory) : this(engine)
        {
            var root = Path.Combine(directory, "SoundMeeter");
            _path = Path.Combine(root, FileName);
            _tempPath = Path.Combine(root, TempFileName);
            _corruptPath = Path.Combine(root, CorruptFileName);
        }

        private string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundMeeter",
            FileName);

        private string _tempPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundMeeter",
            TempFileName);

        private string _corruptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundMeeter",
            CorruptFileName);

        /// <summary>Текущий снимок настроек (выставляется в App.OnStartup после Load).</summary>
        public AppSettings Settings { get; set; } = new();

        /// <summary>Путь к файлу настроек (%APPDATA%\SoundMeeter\settings.json).</summary>
        public string SettingsPath => _path;

        /// <summary>
        /// true, если settings.json записан более новой версией схемы, чем понимает
        /// сборка. В этом режиме фоновое сохранение выключено: файл принадлежит
        /// более новой версии приложения, и затирать его молча нельзя.
        /// </summary>
        public bool HasUnsupportedNewerSchema { get; private set; }

        /// <summary>Версия схемы загруженного файла (для диагностики).</summary>
        public int LoadedSchemaVersion { get; private set; } = -1;

        /// <summary>Что произошло при загрузке (для показа пользователю и в лог).</summary>
        public string LoadReport { get; private set; } = "";

        /// <summary>Синхронное сохранение текущего снимка <see cref="Settings"/>.</summary>
        public void Save() => SaveSync(_engine.CreateSnapshot(), explicitSave: true);

        /// <summary>
        /// Синхронное сохранение. Используется при закрытии окна: вызов из
        /// UI-потока блокирующего async-апдейта (GetAwaiter().GetResult())
        /// приводит к deadlock'у — continuation async-метода пытается вернуться
        /// в UI-поток, который уже заблокирован. Файл настроек небольшой,
        /// поэтому синхронная запись безопасна и проста.
        /// </summary>
        public void SaveSync(AppSettings settings) => SaveSync(settings, explicitSave: true);

        /// <summary>
        /// Синхронная запись с атомарной подменой файла.
        /// </summary>
        /// <param name="explicitSave">
        /// true — сохранение по явному действию пользователя (кнопка, выход из
        /// приложения). Только оно имеет право перезаписать файл, записанный
        /// более новой схемой.
        /// </param>
        public void SaveSync(AppSettings settings, bool explicitSave)
        {
            if (settings == null) return;

            var rules = Settings.PersistentRoutes;
            lock (rules)
            lock (_writeLock)
            {
                if (HasUnsupportedNewerSchema && !explicitSave)
                {
                    _logger.LogWarning(
                        "settings.json записан более новой схемой ({Version}); фоновое сохранение пропущено",
                        LoadedSchemaVersion);
                    return;
                }

                // Снимок движка не содержит правил приложений. Дополняем его актуальными данными.
                settings.PersistentRoutes = rules.Select(r => new DeviceRouteRule
                {
                    ExecutablePath = r.ExecutablePath, DeviceId = r.DeviceId,
                    AppName = r.AppName, IconPath = r.IconPath
                }).ToList();
                settings.HiddenDeviceIds = Settings.HiddenDeviceIds.ToList();
                // Снимок движка про док ничего не знает, ровно как про маршруты
                // приложений: переносим актуальные значения из Settings.
                settings.ObsDock = CloneObsDock(Settings.ObsDock);
                settings.SchemaVersion = SettingsMigrator.CurrentSchemaVersion;
                settings.LogLevel = AppLog.Level.ToString();
                // Снимок движка про язык не знает, ровно как про маршруты приложений
                // и док: язык живёт в Loc, а на диск его кладёт только он сам.
                settings.Language = Loc.RequestedLanguage;

                WriteAtomically(settings);
            }

            HasUnsupportedNewerSchema = false;
            LoadedSchemaVersion = SettingsMigrator.CurrentSchemaVersion;
        }

        public Task SaveAsync(AppSettings settings) => Task.Run(() => SaveSync(settings, explicitSave: false));

        /// <summary>
        /// Глубокая копия настроек дока. Снимок движка идёт на диск из фонового
        /// таймера, а список каналов пользователь правит в окне — без копии
        /// сохранился бы снимок того состояния, которое было при последнем
        /// нажатии «Save».
        /// </summary>
        private static ObsDockSettings CloneObsDock(ObsDockSettings? source)
        {
            if (source == null) return new ObsDockSettings();
            return new ObsDockSettings
            {
                Enabled = source.Enabled,
                Port = source.Port,
                ShowAllInputs = source.ShowAllInputs,
                ShowAllOutputs = source.ShowAllOutputs,
                Channels = source.Channels.Select(c => c.Clone()).ToList()
            };
        }

        public async Task<AppSettings?> LoadAsync()
        {
            if (!File.Exists(_path))
            {
                LoadReport = Loc.Get("Sm.Settings.NoFile");
                _logger.LogInformation("{Report}", LoadReport);
                return null;
            }

            AppSettings? loaded;
            try
            {
                await using var fs = File.OpenRead(_path);
                loaded = await JsonSerializer.DeserializeAsync<AppSettings>(fs);
            }
            catch (Exception ex)
            {
                QuarantineCorruptFile(ex);
                return null;
            }

            if (loaded == null)
            {
                QuarantineCorruptFile(new JsonException("settings.json разбирается в null"));
                return null;
            }

            LoadedSchemaVersion = loaded.SchemaVersion;
            var outcome = SettingsMigrator.Migrate(loaded, out var migrationNote);

            if (outcome == MigrationOutcome.UnsupportedNewerVersion)
            {
                HasUnsupportedNewerSchema = true;
                LoadReport = migrationNote + "; " + Loc.Get("Sm.Settings.NotRewritten");
                _logger.LogWarning("{Report}", LoadReport);
                return null;
            }

            LoadReport = migrationNote.Length > 0 ? migrationNote : Loc.Get("Sm.Settings.Loaded");
            _logger.LogInformation("settings.json загружен (схема {Version}): {Note}",
                LoadedSchemaVersion, migrationNote.Length > 0 ? migrationNote : "без миграций");
            return loaded;
        }

        /// <summary>
        /// Атомарная запись: сначала временный файл рядом с целевым, затем подмена.
        /// <see cref="File.Replace"/> выполняет замену одним системным вызовом, поэтому
        /// читатель (в том числе следующий запуск приложения) никогда не увидит
        /// наполовину записанный JSON.
        /// </summary>
        private void WriteAtomically(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, WriteOptions);
            File.WriteAllText(_tempPath, json);

            try
            {
                if (File.Exists(_path))
                    File.Replace(_tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(_tempPath, _path);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // File.Replace недоступен на некоторых файловых системах — подменяем
                // переименованием с перезаписью. Хуже атомарности, но всё равно
                // лучше прямой записи в целевой файл.
                _logger.LogWarning(ex, "File.Replace недоступен ({Message}), используется Move с перезаписью",
                    ex.Message);
                File.Move(_tempPath, _path, overwrite: true);
            }

            VerifyWrittenFile();
        }

        /// <summary>
        /// Проверка целостности сразу после записи: файл обязан читаться
        /// десериализатором. Молчаливый «успех» с битым содержимым хуже явной ошибки.
        /// </summary>
        private void VerifyWrittenFile()
        {
            try
            {
                using var stream = File.OpenRead(_path);
                var check = JsonSerializer.Deserialize<AppSettings>(stream);
                if (check == null)
                    _logger.LogError("{File} записан, но не разбирается в AppSettings", _path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{File} записан, но не читается ({Message})", _path, ex.Message);
            }
        }

        /// <summary>
        /// Нечитаемый settings.json не удаляем, а откладываем рядом под именем
        /// settings.corrupt.json: файл нужен для разбора, почему сломался.
        /// </summary>
        private void QuarantineCorruptFile(Exception cause)
        {
            LoadReport = Loc.Get("Sm.Settings.Corrupt", cause.Message, CorruptFileName);
            _logger.LogWarning(cause, "settings.json не читается; откладываем как {File}", CorruptFileName);

            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(_corruptPath)) File.Delete(_corruptPath);
                File.Move(_path, _corruptPath);
            }
            catch (Exception ex)
            {
                // Не смогли отложить — читаем как есть, приложение всё равно
                // стартует с настройками по умолчанию.
                _logger.LogError(ex, "Не удалось отложить повреждённый settings.json как {File}", CorruptFileName);
            }
        }
    }
}