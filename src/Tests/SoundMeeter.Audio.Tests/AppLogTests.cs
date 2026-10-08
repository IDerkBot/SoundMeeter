using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Text;
using Xunit;

namespace SoundMeeter.Audio.Tests;

/// <summary>
/// Фильтр уровня в окне просмотра журнала.
///
/// Тесты пишут файл НАСТОЯЩИМ провайдером, а не собранными вручную строками.
/// Иначе проверка фильтра проверяла бы не тот формат, который реально пишется:
/// сокращения уровней («Info », «Warn » — пять символов, чтобы не прыгала
/// колонка) живут в <see cref="RotatingFileLoggerProvider"/>, и если они разъедутся
/// с разбором в фильтре, строки начнут молча попадать не туда.
/// </summary>
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir;

    public AppLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "smlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Временный каталог: убрать не смогли — не повод ронять прогон.
        }
    }

    /// <summary>Пишет указанные записи настоящим провайдером и возвращает путь к файлу.</summary>
    private string WriteLog(params (LogLevel Level, string Message)[] records)
    {
        var provider = new RotatingFileLoggerProvider(_dir, LogLevel.Trace);
        try
        {
            foreach (var (level, message) in records)
                provider.Write(level, "Test", message, null);

            return provider.CurrentFilePath;
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void TraceShowsEverything()
    {
        var path = WriteLog(
            (LogLevel.Trace, "t"),
            (LogLevel.Debug, "d"),
            (LogLevel.Information, "i"),
            (LogLevel.Warning, "w"),
            (LogLevel.Error, "e"),
            (LogLevel.Critical, "c"));

        var text = AppLog.ReadLog(path, minimumLevel: LogLevel.Trace);

        foreach (var message in new[] { "t", "d", "i", "w", "e", "c" })
            Assert.Contains(message, text);
    }

    [Theory]
    [InlineData(LogLevel.Debug, new[] { "d", "i", "w", "e", "c" }, new[] { "t" })]
    [InlineData(LogLevel.Information, new[] { "i", "w", "e", "c" }, new[] { "t", "d" })]
    [InlineData(LogLevel.Warning, new[] { "w", "e", "c" }, new[] { "t", "d", "i" })]
    [InlineData(LogLevel.Error, new[] { "e", "c" }, new[] { "t", "d", "i", "w" })]
    public void FilterKeepsSelectedLevelAndAbove(LogLevel minimum, string[] kept, string[] dropped)
    {
        var path = WriteLog(
            (LogLevel.Trace, "t"),
            (LogLevel.Debug, "d"),
            (LogLevel.Information, "i"),
            (LogLevel.Warning, "w"),
            (LogLevel.Error, "e"),
            (LogLevel.Critical, "c"));

        var text = AppLog.ReadLog(path, minimumLevel: minimum);

        foreach (var message in kept) Assert.Contains($": {message}", text);
        foreach (var message in dropped) Assert.DoesNotContain($": {message}", text);
    }

    /// <summary>
    /// Записи без метки уровня фильтр не трогает. Иначе из отчёта об ошибке
    /// выпал бы стектрейс и любой многострочный текст — то есть ровно то, ради
    /// чего журнал открывают.
    /// </summary>
    [Fact]
    public void LinesWithoutLevelMarkerSurviveTheFilter()
    {
        var path = WriteLog((LogLevel.Information, "i"), (LogLevel.Error, "e"));
        File.AppendAllText(path, "   at Some.Method()\r\n   at Other.Method()\r\n");

        var text = AppLog.ReadLog(path, minimumLevel: LogLevel.Error);

        // Запись Info под порогом уходит, Error остаётся...
        Assert.DoesNotContain(": i", text);
        Assert.Contains(": e", text);
        // ...а строки без метки уровня остаются в любом случае.
        Assert.Contains("at Some.Method()", text);
        Assert.Contains("at Other.Method()", text);
    }

    /// <summary>
    /// Фильтр должен стойко пережить формат: метка уровня — фиксированные
    /// смещения, и любая правка формата (длина даты, ширина токена) обязана
    /// ломать тест, а не тихо отключать фильтр.
    /// </summary>
    [Fact]
    public void EveryLevelTokenSurvivesRoundTripThroughTheProvider()
    {
        var levels = new[]
        {
            LogLevel.Trace, LogLevel.Debug, LogLevel.Information,
            LogLevel.Warning, LogLevel.Error, LogLevel.Critical
        };
        var path = WriteLog(levels.Select(l => (l, l.ToString())).ToArray());

        var raw = AppLog.ReadLog(path);
        foreach (var level in levels)
            Assert.Contains($"[{Abbreviated(level)}]", raw);

        // Порог «Error» обязан оставить Error и Critical, и только их.
        var filtered = AppLog.ReadLog(path, minimumLevel: LogLevel.Error);
        foreach (var level in levels)
        {
            if (level >= LogLevel.Error) Assert.Contains($": {level}", filtered);
            else Assert.DoesNotContain($": {level}", filtered);
        }
    }

    [Fact]
    public void EmptyAndMissingFilesDoNotThrow()
    {
        var missing = Path.Combine(_dir, "no-such.log");
        Assert.Contains("Не удалось прочитать журнал", AppLog.ReadLog(missing));

        var empty = Path.Combine(_dir, "empty.log");
        File.WriteAllText(empty, "");
        Assert.Equal("", AppLog.ReadLog(empty));
    }

    /// <summary>Сокращение уровня из <c>RotatingFileLoggerProvider.Abbreviate</c>.</summary>
    private static string Abbreviated(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info ",
        LogLevel.Warning => "Warn ",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Crit ",
        _ => "None "
    };
}