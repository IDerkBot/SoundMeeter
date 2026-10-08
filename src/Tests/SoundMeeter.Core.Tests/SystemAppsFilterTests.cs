using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Text.Json;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Фильтр системных приложений живёт файлом рядом с exe (SM-E03).
///
/// Публикация одним файлом ничего не оставляет рядом с exe, поэтому сервис
/// создаёт файл сам — из встроенной в сборку копии того же файла. Дальше файл
/// принадлежит пользователю: его правят руками, и они переживают обновление
/// (обновление удаляет рядом только остатки сборки, dll/pdb).
///
/// Приоритет источников: файл пользователя → встроенная копия → пустой фильтр.
/// Тесты проверяют именно его, потому что сломанный приоритет выглядит как
/// «приложения фильтруются неправильно», и найти его можно только здесь.
/// </summary>
public class SystemAppsFilterTests
{
    /// <summary>
    /// Встроенная копия нужна как образец для создания файла: это он делает
    /// списком ключевых слов по умолчанию.
    /// </summary>
    [Fact]
    public void EmbeddedCopyHoldsTheDefaultKeywords()
    {
        // Именно сборка сервиса: ресурс лежит в SoundMeeter.Core, а не в тестах.
        using var stream = typeof(InstalledAppsService).Assembly
            .GetManifestResourceStream(InstalledAppsService.FilterResource);

        Assert.NotNull(stream);

        var config = JsonSerializer.Deserialize<SystemAppsFilterConfig>(stream!);

        Assert.NotNull(config);
        Assert.NotEmpty(config!.SystemNameKeywords);
    }

    /// <summary>
    /// Файла нет — сервис обязан его создать и взять ключевые слова из встроенной
    /// копии. Ставится наоборот: пользовательский файл важнее встроенной копии.
    /// </summary>
    [Fact]
    public void MissingFileIsCreatedFromTheEmbeddedCopy()
    {
        string path = InstalledAppsService.FilterPath;
        bool existed = File.Exists(path);
        byte[]? saved = existed ? File.ReadAllBytes(path) : null;

        try
        {
            if (existed) File.Delete(path);

            _ = new InstalledAppsService();

            Assert.True(File.Exists(path), "сервис должен создать файл фильтра рядом с exe");

            var config = JsonSerializer.Deserialize<SystemAppsFilterConfig>(File.ReadAllText(path));
            Assert.NotEmpty(config!.SystemNameKeywords);
        }
        finally
        {
            // Каталог вывода тестов — не место для мусора: возвращаем как было.
            if (saved != null) File.WriteAllBytes(path, saved);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Пользовательский файл главнее встроенной копии: иначе правки руками
    /// молча затирались бы на каждом запуске.
    /// </summary>
    [Fact]
    public void EditedFileWinsOverTheEmbeddedCopy()
    {
        string path = InstalledAppsService.FilterPath;
        byte[]? saved = File.Exists(path) ? File.ReadAllBytes(path) : null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                """{ "SystemNameKeywords": [ "Мой фильтр" ], "SystemPublisherKeywords": [ "Мой издатель" ] }""");

            _ = new InstalledAppsService();

            var config = JsonSerializer.Deserialize<SystemAppsFilterConfig>(File.ReadAllText(path));

            Assert.Equal(["Мой фильтр"], config!.SystemNameKeywords);
        }
        finally
        {
            if (saved != null) File.WriteAllBytes(path, saved);
            else if (File.Exists(path)) File.Delete(path);
        }
    }
}