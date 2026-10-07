using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Security;
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

        /// <summary>Путь резервной копии рядом с оригиналом.</summary>
        public string BackupPath => _path + ".bak";

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

            // Порядок захвата (список правил → запись) обязателен: AddPersistentRoute
            // тоже держит список правил и зовёт Save() внутри, то есть запросы идут
            // строго в этом порядке. Обратный дал бы взаимоблокировку.
            lock (Settings.PersistentRoutes)
            lock (_writeLock)
            {
                if (HasUnsupportedNewerSchema && !explicitSave)
                {
                    _logger.LogWarning(
                        "settings.json записан более новой схемой ({Version}); фоновое сохранение пропущено",
                        LoadedSchemaVersion);
                    return;
                }

                ApplyAppLevelFields(settings);
                WriteAtomically(settings);
            }

            HasUnsupportedNewerSchema = false;
            LoadedSchemaVersion = SettingsMigrator.CurrentSchemaVersion;
        }

        /// <summary>
        /// Полный снимок всех настроек — ровно то, что уходит на диск, только без
        /// записи. Основа экспорта: экспортировать <see cref="IAudioEngine.CreateSnapshot"/>
        /// нельзя, движок не знает ни про маршруты приложений, ни про док, ни про
        /// язык, ни про поведение приложения — такой экспорт потерял бы восемь
        /// полей из тринадцати.
        /// </summary>
        public AppSettings CreateFullSnapshot()
        {
            var snapshot = _engine.CreateSnapshot();

            lock (Settings.PersistentRoutes)
            lock (_writeLock)
            {
                ApplyAppLevelFields(snapshot);
            }

            return snapshot;
        }

        /// <summary>
        /// Резервная копия текущих настроек в <see cref="BackupPath"/>. Нужна перед
        /// импортом: он заменяет всё разом, а откатить замену без копии нечем.
        /// Возвращает путь или null, если записать не удалось — импорт при этом
        /// не отменяется: пользователь о таком риске предупреждён заранее.
        /// </summary>
        public string? BackupCurrent()
        {
            var snapshot = CreateFullSnapshot();

            lock (Settings.PersistentRoutes)
            lock (_writeLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_path)!;
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshot, WriteOptions));
                    _logger.LogInformation("Резервная копия настроек: {Path}", BackupPath);
                    return BackupPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or NotSupportedException or SecurityException)
                {
                    _logger.LogError(ex, "Резервная копия настроек не создана: {Message}", ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Переносит в живой снимок те поля, которых нет у снимка движка, —
        /// зеркало <see cref="ApplyAppLevelFields"/> для импорта. Применяет только
        /// данные: язык и уровень журнала живут в Loc и AppLog, а док и автозапуск
        /// меняют состояние системы, поэтому их доведёт до конца вызывающий.
        /// </summary>
        public void AdoptAppLevelFields(AppSettings imported)
        {
            if (imported == null) return;

            var live = Settings;
            lock (live.PersistentRoutes)
            {
                live.PersistentRoutes = (imported.PersistentRoutes ?? new List<DeviceRouteRule>())
                    .Select(CloneRoute).ToList();
                live.HiddenDeviceIds = imported.HiddenDeviceIds?.ToList() ?? new List<string>();
                live.ObsDock = CloneObsDock(imported.ObsDock);
                live.TextToSpeech = CloneTextToSpeech(imported.TextToSpeech);
                live.Twitch = AdoptTwitchOnImport(imported.Twitch);
                live.Language = imported.Language ?? "";
                live.TrayEnabled = imported.TrayEnabled;
                live.RunAtStartup = imported.RunAtStartup;
                live.SchemaVersion = SettingsMigrator.CurrentSchemaVersion;
            }
        }

        /// <summary>
        /// Дополняет снимок движка данными, которых движок не знает. Список
        /// правил приложений общий с <see cref="AudioService"/>, который правит
        /// его на своих потоках, поэтому вызывающий держит его под замком.
        /// </summary>
        private void ApplyAppLevelFields(AppSettings settings)
        {
            var live = Settings;

            settings.PersistentRoutes = live.PersistentRoutes.Select(CloneRoute).ToList();
            settings.HiddenDeviceIds = live.HiddenDeviceIds.ToList();
            // Снимок движка про док ничего не знает, ровно как про маршруты
            // приложений: переносим актуальные значения из Settings.
            settings.ObsDock = CloneObsDock(live.ObsDock);
            // То же и для модуля синтеза речи (SM-E01): список голосов
            // пользователей правится прямо в чате командой !ttsvoice, то есть
            // мимо кнопки «Применить», и без копии сохранился бы снимок того
            // состояния, которое было при последнем нажатии.
            settings.TextToSpeech = CloneTextToSpeech(live.TextToSpeech);
            settings.Twitch = (live.Twitch ?? new TwitchSettings()).Clone();
            settings.SchemaVersion = SettingsMigrator.CurrentSchemaVersion;
            settings.LogLevel = AppLog.Level.ToString();
            // Снимок движка про язык не знает, ровно как про маршруты приложений
            // и док: язык живёт в Loc, а на диск его кладёт только он сам.
            settings.Language = Loc.RequestedLanguage;
            // То же с поведением приложения (SM-D01/SM-D02): движок о них не
            // знает, а без переноса первое же фоновое сохранение (через 5 с
            // после старта) стёрло бы их, и настройки «откатывались» бы сами.
            settings.TrayEnabled = live.TrayEnabled;
            settings.RunAtStartup = live.RunAtStartup;
        }

        private static DeviceRouteRule CloneRoute(DeviceRouteRule rule) => new()
        {
            ExecutablePath = rule.ExecutablePath, DeviceId = rule.DeviceId,
            AppName = rule.AppName, IconPath = rule.IconPath
        };

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

        /// <summary>
        /// Глубокая копия настроек синтеза речи. Списки голосов и игнора копируются
        /// поштучно: они правятся и из окна настроек, и из чата, а список, взятый
        /// ссылкой, сохранил бы в файл всё, что туда успели накидать с тех пор.
        /// </summary>
        private static TextToSpeechSettings CloneTextToSpeech(TextToSpeechSettings? source)
        {
            if (source == null) return new TextToSpeechSettings();
            return new TextToSpeechSettings
            {
                Enabled = source.Enabled,
                TargetInputId = source.TargetInputId,
                TargetInputDeviceId = source.TargetInputDeviceId,
                DefaultVoice = source.DefaultVoice,
                Rate = source.Rate,
                Volume = source.Volume,
                SpeakCommand = source.SpeakCommand,
                VoiceCommand = source.VoiceCommand,
                AllowVoiceChange = source.AllowVoiceChange,
                MaxQueueLength = source.MaxQueueLength,
                MaxMessagesPerMinute = source.MaxMessagesPerMinute,
                MaxMessageChars = source.MaxMessageChars,
                UserVoices = source.UserVoices?.Select(pair => pair.Clone()).ToList() ?? new List<TtsUserVoice>(),
                IgnoredUsers = source.IgnoredUsers?.ToList() ?? new List<string>(),
            };
        }

        /// <summary>
        /// Переносит настройки Twitch при импорте, и это НЕ простая копия.
        ///
        /// Признаки входа (<see cref="TwitchSettings.AuthorizedLogin"/> и кэш
        /// <see cref="TwitchSettings.ChannelUserId"/>) описывают состояние ВИДИМОГО
        /// компьютера: токен лежит в отдельном зашифрованном файле, и в
        /// экспортируемый settings.json он не попадает. Перенести «кем вошли» без
        /// токена — значит показать в окне подключение, которого нет: модуль
        /// пойдёт в API с пустым токеном и откажет. Поэтому вход при импорте
        /// сбрасывается, а пожелания пользователя (канал, включатель чата, опрос
        /// статуса) переносятся — их пользователь настраивал сознательно.
        ///
        /// Кэш ChannelUserId при этом берётся не из файла, а обнуляется: он
        /// адресует подписку на конкретный канал, и после импорта канал может быть
        /// уже другим. Он заполнится сам при первом подключении.
        /// </summary>
        private static TwitchSettings AdoptTwitchOnImport(TwitchSettings? imported)
        {
            var result = imported?.Clone() ?? new TwitchSettings();
            result.AuthorizedLogin = "";
            result.ChannelUserId = "";
            return result;
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