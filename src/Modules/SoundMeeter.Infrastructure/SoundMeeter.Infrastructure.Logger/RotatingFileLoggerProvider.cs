using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace SoundMeeter.Services.Logging;

/// <summary>
/// Поставщик логирования в ротируемый текстовый файл.
///
/// Зачем свой, а не <c>Microsoft.Extensions.Logging.File</c>: тот удалён из
/// экосистемы .NET, а требования к журналу у приложения конкретные — дата в имени
/// файла, ограничение «5 МБ × 3 файла», проверка при старте и живая перечитываемость
/// файла из окна просмотра лога (поэтому файл открыт на чтение «всем желающим»).
///
/// Формат строки: <c>2026-09-27 11:22:33.123 [Warn ] Category: сообщение</c>.
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    /// <summary>Максимальный размер одного файла журнала (5 МБ).</summary>
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    /// <summary>Сколько файлов журнала храним вместе с текущим.</summary>
    public const int MaxFiles = 3;

    private const string Prefix = "soundmeeter-";

    private readonly string _directory;
    private readonly object _gate = new();
    private volatile LogLevel _minimumLevel;

    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;
    private string? _rotationError;

    public RotatingFileLoggerProvider(string directory, LogLevel minimumLevel)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;

        Directory.CreateDirectory(_directory);
        PruneOldLogs();

        // Проверка размера при старте: если вчерашний/сегодняшний файл уже переполнен,
        // он уходит в архив до того, как приложение начнёт писать.
        if (File.Exists(CurrentFilePath) && new FileInfo(CurrentFilePath).Length >= MaxFileSizeBytes)
            Roll();

        EnsureWriter();
    }

    /// <summary>Каталог с журналами (%LOCALAPPDATA%\SoundMeeter\logs).</summary>
    public string DirectoryPath => _directory;

    /// <summary>Последняя ошибка ротации (null — ротация прошла или ещё не была нужна).</summary>
    public string? RotationError => _rotationError;

    /// <summary>Файл, в который пишется прямо сейчас (имя содержит дату).</summary>
    public string CurrentFilePath { get; private set; } = "";

    /// <summary>Ниже этого уровня записи отбрасываются. Меняется на лету из настроек.</summary>
    public LogLevel MinimumLevel => _minimumLevel;

    public void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public bool IsEnabled(LogLevel logLevel) =>
        !_disposed && logLevel != LogLevel.None && logLevel >= _minimumLevel;

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed) return;

        string line = Format(level, category, message, exception);

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                EnsureWriter();
                if (_writer == null) return;

                _writer.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;

                // Ротация по мере записи: файл не должен расти бесконечно, даже если
                // приложение живёт неделями без перезапуска.
                if (_written >= MaxFileSizeBytes) Roll();
            }
            catch (Exception)
            {
                // Логирование не должно ронять приложение. Пишем в никуда,
                // но не зацикливаемся: следующая попытка будет при следующей записи.
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Закрытие журнала не влияет на завершение приложения.
            }
            _writer = null;
        }
    }

    private static string Format(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder(96);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [");
        sb.Append(Abbreviate(level));
        sb.Append("] ");
        sb.Append(category);
        sb.Append(": ");
        sb.Append(message);
        if (exception != null)
        {
            sb.Append(" | ");
            sb.Append(exception.GetType().Name);
            sb.Append(": ");
            sb.Append(exception.Message);
        }
        return sb.ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info ",
        LogLevel.Warning => "Warn ",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Crit ",
        _ => "None "
    };

    private void EnsureWriter()
    {
        if (_writer != null) return;

        CurrentFilePath = Path.Combine(_directory, Prefix + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
        // FileShare.ReadWrite|Delete — файл одновременно открыт окном просмотра лога
        // и может быть переименован при ротации.
        var stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _written = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <summary>Закрывает текущий файл и продолжает писать в новый (с датой).</summary>
    private void Roll()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Писатель уже мог быть закрыт — это не мешает сдвинуть архив.
        }
        _writer = null;

        try
        {
            var date = DateTime.Now;
            var source = CurrentFilePath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;

            // Сдвигаем архив вверх: .1 → .2, …, .(n-1) → .n; самый старый удаляется.
            // Вместе с текущим файлом остаётся ровно MaxFiles файлов журнала.
            for (int i = MaxFiles - 2; i >= 1; i--)
            {
                var from = ArchivePath(date, i);
                if (!File.Exists(from)) continue;
                File.Move(from, ArchivePath(date, i + 1), overwrite: true);
            }

            File.Move(source, ArchivePath(date, 1), overwrite: true);
        }
        catch (Exception ex)
        {
            // Ротация не должна ронять работу: следующая запись попадёт в текущий файл.
            _rotationError = ex.Message;
        }
    }

    private string ArchivePath(DateTime date, int index) =>
        Path.Combine(_directory, Prefix + date.ToString("yyyy-MM-dd") + "." + index + ".log");

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Занятый файл (например, открыт в блокноте) — пропускаем.
        }
    }

    /// <summary>
    /// Удаляет всё, кроме <see cref="MaxFiles"/> самых свежих файлов журнала.
    /// Без этого каталог рос бы на одну тройку файлов за каждый день работы.
    /// </summary>
    private void PruneOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(_directory)
                .GetFiles(Prefix + "*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (int i = MaxFiles - 1; i < files.Count; i++) Delete(files[i].FullName);
        }
        catch
        {
            // Недоступный каталог логов не должен мешать запуску приложения.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly RotatingFileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(RotatingFileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
