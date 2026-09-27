using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.IO;

namespace SoundMeeter.Services.Logging;

/// <summary>
/// Централизованная точка входа в журнал приложения (SM-A03).
///
/// Раньше единственным следом события был <c>Debug.WriteLine</c>, который компилятор
/// вырезает в Release: у пользователя не оставалось ни одного свидетельства об
/// открытии устройств, об ошибках WASAPI и о работе автообновления. Теперь всё
/// идёт сюда и пишется в ротируемый файл, а не только в окно отладчика.
///
/// Класс статический намеренно: журналом пользуются и объекты, которые создаёт
/// движок вручную из аудиопотока (<see cref="Audio.InputSource"/>,
/// <see cref="Audio.BusTap"/>, <see cref="Audio.DenoiserDsp"/>) — протягивать
/// туда <c>ILogger</c> через четыре конструктора значило бы усложнять горячий путь
/// без выигрыша. Сами логгеры — обычные <see cref="ILogger"/> поверх
/// <see cref="RotatingFileLoggerProvider"/>, то есть Microsoft.Extensions.Logging.
/// </summary>
public static class AppLog
{
    private static readonly ConcurrentDictionary<string, ILogger> Loggers = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    private static ILoggerFactory? _factory;
    private static RotatingFileLoggerProvider? _provider;
    private static ILogger _root = NullLogger.Instance;

    /// <summary>Каталог журналов: %LOCALAPPDATA%\SoundMeeter\logs.</summary>
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundMeeter",
        "logs");

    /// <summary>Текущий уровень журнала (в Release по умолчанию Information).</summary>
    public static LogLevel Level { get; private set; } = LogLevel.Information;

    /// <summary>Файл, в который пишется прямо сейчас (пусто, если журнал не поднят).</summary>
    public static string CurrentFilePath => _provider?.CurrentFilePath ?? "";

    /// <summary>Уровни, которые можно выбрать в настройках.</summary>
    public static IReadOnlyList<string> AvailableLevels { get; } = new[]
    {
        "Trace", "Debug", "Information", "Warning", "Error"
    };

    /// <summary>
    /// Поднимает журнал. Вызывается первой строкой OnStartup — раньше любой
    /// диагностики, иначе ранние ошибки (например, падение в SettingsService)
    /// снова останутся без следа.
    /// </summary>
    public static void Initialize(LogLevel level)
    {
        lock (Gate)
        {
            if (_factory != null)
            {
                SetLevel(level);
                return;
            }

            try
            {
                _provider = new RotatingFileLoggerProvider(DirectoryPath, level);
                _factory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Trace);
                    builder.AddProvider(_provider);
                });
                _root = _factory.CreateLogger("SoundMeeter");
                Level = level;
            }
            catch (Exception ex)
            {
                // Нет доступа к каталогу журналов — не повод не запускать микшер.
                _root = NullLogger.Instance;
                _provider = null;
                _factory = null;
                TraceFallback($"logging initialization failed: {ex.Message}");
            }
        }

        _root.LogInformation("SoundMeeter {Version} starting; log={File} level={Level}",
            GetVersion(), CurrentFilePath, Level);
    }

    /// <summary>Переключает детализацию журнала (из настроек, без перезапуска).</summary>
    public static void SetLevel(LogLevel level)
    {
        Level = level;
        _provider?.SetMinimumLevel(level);
    }

    /// <summary>Переводит имя уровня из настроек в <see cref="LogLevel"/>.</summary>
    public static LogLevel ParseLevel(string? name) =>
        Enum.TryParse<LogLevel>(name, ignoreCase: true, out var level) ? level : LogLevel.Information;

    /// <summary>Логгер по типу: имя категории = имя класса.</summary>
    public static ILogger For<T>() => For(typeof(T));

    public static ILogger For(Type type) => For(type.Name);

    public static ILogger For(string category) =>
        Loggers.GetOrAdd(category, name => _factory?.CreateLogger(name) ?? (ILogger)NullLogger.Instance);

    /// <summary>Список файлов журнала, свежие сверху (для окна просмотра и диагностики).</summary>
    public static IReadOnlyList<string> LogFiles()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<string>();
            return new DirectoryInfo(DirectoryPath)
                .GetFiles("soundmeeter-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Читает файл журнала для окна просмотра. Файл открыт провайдером на запись,
    /// поэтому читаем с FileShare.ReadWrite.
    /// </summary>
    public static string ReadLog(string path, int maxBytes = 512 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"Не удалось прочитать журнал: {ex.Message}";
        }
    }

    /// <summary>
    /// Текст для «Скопировать диагностику»: версия, уровень, список устройств
    /// и хвост текущего журнала.
    /// </summary>
    public static string BuildDiagnosticsText(string devices, string? note = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SoundMeeter {GetVersion()}");
        sb.AppendLine($"Сборка: {Environment.ProcessPath ?? "?"}");
        sb.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Процесс: {Environment.ProcessId}, .NET {Environment.Version}");
        sb.AppendLine($"Уровень журнала: {Level}");
        sb.AppendLine($"Файл журнала: {CurrentFilePath}");
        if (!string.IsNullOrWhiteSpace(note)) sb.AppendLine(note);
        sb.AppendLine();
        sb.AppendLine("=== Устройства ===");
        sb.Append(devices);
        sb.AppendLine();
        sb.AppendLine("=== Журнал (хвост) ===");
        var file = CurrentFilePath;
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) sb.Append(ReadLog(file));
        else sb.AppendLine("(журнал ещё не создан)");
        return sb.ToString();
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _root?.LogInformation("SoundMeeter stopping"); } catch { }
            try { _factory?.Dispose(); } catch { }
            _factory = null;
            _provider = null;
            _root = NullLogger.Instance;
            Loggers.Clear();
        }
    }

    private static string GetVersion() =>
        typeof(AppLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// Аварийный канал для ситуаций, когда провайдер не поднялся: событие видно
    /// в отладчике, а не теряется молча.
    /// </summary>
    private static void TraceFallback(string message) =>
        System.Diagnostics.Debug.WriteLine("SoundMeeter: " + message);
}
