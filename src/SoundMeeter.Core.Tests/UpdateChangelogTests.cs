using SoundMeeter.Models;
using SoundMeeter.Services;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Сборка описания цепочки релизов (SM-E03).
///
/// Обновление с 0.0.1 сразу на 0.0.7 должно показать, что было и в 0.0.2…0.0.6, а
/// не только записи последнего релиза. Проверяется сам текст: заголовок каждой
/// версии, порядок (от новых к старым), подстановка текста «описания нет» и то,
/// что разметка релизов не теряется при склейке.
/// </summary>
public class UpdateChangelogTests
{
    private static UpdateRelease Release(string tag, string notes,
        DateTimeOffset? published = null) => new()
    {
        Version = AppVersion.TryParse(tag, out var version) ? version : new AppVersion(0, 0, 0),
        TagName = tag,
        ReleaseNotes = notes,
        PublishedAt = published
    };

    [Fact]
    public void EveryMissedVersionGetsItsOwnHeading()
    {
        var changelog = new[]
        {
            Release("0.0.7", "## Исправления\n- Поправлен выбор логов."),
            Release("0.0.6", "## Исправления\n- Поправлена фильтрация."),
            Release("0.0.5", "## Улучшения\n- Появился эквалайзер.")
        };

        string text = UpdateChangelog.Compose(changelog, "описания нет");

        // Заголовок верхнего уровня у каждой версии: разделы самого релиза (##)
        // остаются вложенными под ним.
        Assert.Equal(
            "# 0.0.7\n\n## Исправления\n- Поправлен выбор логов.\n\n" +
            "# 0.0.6\n\n## Исправления\n- Поправлена фильтрация.\n\n" +
            "# 0.0.5\n\n## Улучшения\n- Появился эквалайзер.",
            text);
    }

    [Fact]
    public void ReleaseDateIsShownWithTheVersion()
    {
        var changelog = new[]
        {
            Release("0.0.7", "Что-то.", new DateTimeOffset(2026, 10, 4, 20, 30, 0, TimeSpan.Zero))
        };

        Assert.StartsWith("# 0.0.7 (2026-10-04)", UpdateChangelog.Compose(changelog, "описания нет"));
    }

    /// <summary>
    /// Релиз без заметок всё равно должен быть виден: пустой заголовок означал бы
    /// потерянную версию, а подпись «описания нет» честно говорит, что записей нет.
    /// </summary>
    [Fact]
    public void ReleaseWithoutNotesGetsThePlaceholder()
    {
        var changelog = new[]
        {
            Release("0.0.7", "   "),
            Release("0.0.6", "Поправлено.")
        };

        string text = UpdateChangelog.Compose(changelog, "описания нет");

        Assert.Equal("# 0.0.7\n\nописания нет\n\n# 0.0.6\n\nПоправлено.", text);
    }

    [Fact]
    public void EmptyChangelogProducesEmptyText()
    {
        Assert.Equal("", UpdateChangelog.Compose([], "описания нет"));
    }
}