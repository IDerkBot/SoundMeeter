using SoundMeeter.Models;
using SoundMeeter.Services;
using Xunit;

namespace SoundMeeter.Update.Tests;

/// <summary>
/// План установки обновления и его главная проверка — «это вообще сборка?».
///
/// Раньше ответом на вопрос был отдельный файл SoundMeeter.dll рядом с exe, и
/// публикация одним файлом (PublishSingleFile) ломала обновление целиком: архив
/// с единственным exe отвергался как неполный. Теперь такой архив принимается, но
/// только если он действительно похож на бандл, а не на огрызок: рядом с exe
/// ничего нет, и сам exe велик.
///
/// Архивы собираются вручную (MZ-заголовок плюс хвост нужного размера) — Plan
/// проверяет именно структуру файла, а не его происхождение.
/// </summary>
public class UpdatePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "SoundMeeterPlanTests_" + Guid.NewGuid().ToString("N"));

    private string Payload => Path.Combine(_root, "payload");
    private string Install => Path.Combine(_root, "install");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста не должна ронять прогон из-за антивируса.
        }
    }

    private static UpdateInfo Update => new() { Version = new AppVersion(0, 1, 2), TagName = "0.1.2" };

    /// <summary>
    /// Настоящий PE-файл нужного размера: заголовок MZ плюс хвост. Настоящей
    /// сборки тут быть не может — она весит десятки мегабайт и проверяется
    /// структурно.
    /// </summary>
    private static void WriteExecutable(string path, int sizeInMegabytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = new byte[sizeInMegabytes * 1024 * 1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
    }

    private UpdatePlan Plan() => UpdateApplier.Plan(
        Payload, Install, Path.Combine(Install, "SoundMeeter.exe"),
        Path.Combine(_root, "updates"), Update);

    [Fact]
    public void SingleFileBundleIsAccepted()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 16);

        var plan = Plan();

        Assert.Equal(1, plan.NewFileCount);
        Assert.Empty(plan.FilesToDelete);
    }

    /// <summary>
    /// Переход с обычной многофайловой сборки на single-file: DLL и PDB исчезают.
    /// Сюда попадает и старая satellite-сборка локализации — оставленная рядом с новым
    /// exe, она перебила бы локализацию, вшитую в сам exe.
    /// </summary>
    [Fact]
    public void OldFilesOfAMultiFileInstallAreRemoved()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 16);

        Directory.CreateDirectory(Install);
        WriteExecutable(Path.Combine(Install, "SoundMeeter.exe"), 1);
        File.WriteAllText(Path.Combine(Install, "SoundMeeter.Core.dll"), "старая сборка");
        File.WriteAllText(Path.Combine(Install, "StreamerTools.Style.pdb"), "старые символы");
        Directory.CreateDirectory(Path.Combine(Install, "ru"));
        File.WriteAllText(Path.Combine(Install, "ru", "SoundMeeter.ChangeLanguage.resources.dll"), "старый язык");

        var plan = Plan();

        Assert.Equal(3, plan.FilesToDelete.Count);
        Assert.Contains(plan.FilesToDelete, file => Path.GetFileName(file) == "SoundMeeter.Core.dll");
        Assert.Contains(plan.FilesToDelete, file => Path.GetFileName(file) == "StreamerTools.Style.pdb");
        Assert.Contains(plan.FilesToDelete, file => Path.GetFileName(file) == "SoundMeeter.ChangeLanguage.resources.dll");
    }

    /// <summary>
    /// Главное правило обновления: файлы удаляются только по типу — dll и pdb.
    /// Папки остаются на месте (пустыми), json, который пользователь правит, и его
    /// собственные файлы не трогаются никогда: иначе автообновление было бы «удали
    /// всё, чего нет в архиве».
    /// </summary>
    [Fact]
    public void EverythingExceptAssembliesIsLeftInPlace()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 16);

        Directory.CreateDirectory(Install);
        WriteExecutable(Path.Combine(Install, "SoundMeeter.exe"), 1);
        File.WriteAllText(Path.Combine(Install, "SoundMeeter.Core.dll"), "остаток сборки");

        // Пользовательские файлы и папки рядом с exe.
        Directory.CreateDirectory(Path.Combine(Install, "Resources"));
        File.WriteAllText(Path.Combine(Install, "Resources", "system_apps_filter.json"), "мой список");
        File.WriteAllText(Path.Combine(Install, "my-notes.txt"), "мои заметки");
        File.WriteAllText(Path.Combine(Install, "settings.json"), "мои настройки");
        Directory.CreateDirectory(Path.Combine(Install, "logs"));
        File.WriteAllText(Path.Combine(Install, "logs", "2026-10-05.log"), "журнал");

        var plan = Plan();

        Assert.Equal("SoundMeeter.Core.dll", Path.GetFileName(Assert.Single(plan.FilesToDelete)));
        Assert.True(Directory.Exists(Path.Combine(Install, "Resources")));
        Assert.True(Directory.Exists(Path.Combine(Install, "logs")));
    }

    /// <summary>
    /// Обычная многофайловая сборка продолжает приниматься: рядом с exe лежит
    /// SoundMeeter.dll, и проверка на «управляющую сборку» остаётся в силе.
    /// </summary>
    [Fact]
    public void ClassicPortableBuildIsStillAccepted()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 1);
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.dll"), 1);
        Directory.CreateDirectory(Install);

        var plan = Plan();

        Assert.Equal(2, plan.NewFileCount);
    }

    /// <summary>
    /// Одинокий маленький exe без управляющей сборки — это не сборка, а обрывок
    /// архива: раньше такой отвергался, и должен отвергаться по-прежнему.
    /// </summary>
    [Fact]
    public void LoneSmallExecutableIsRejected()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 1);

        Assert.Throws<InvalidOperationException>(() => Plan());
    }

    /// <summary>
    /// Рядом с exe в архиве лежит редактируемый system_apps_filter.json — и это
    /// норма: он не сборка, и его наличие не делает релиз неполным. Именно так
    /// выглядит настоящий portable-архив.
    /// </summary>
    [Fact]
    public void BundleWithAnEditableFileNextToItIsAccepted()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 16);
        Directory.CreateDirectory(Path.Combine(Payload, "Resources"));
        File.WriteAllText(Path.Combine(Payload, "Resources", "system_apps_filter.json"), "список");

        var plan = Plan();

        Assert.Equal(2, plan.NewFileCount);
        Assert.Empty(plan.FilesToDelete);
    }

    /// <summary>
    /// А вот чужая dll рядом с exe означает ровно обратное: сборка разъехалась
    /// (частично распакованный архив, не single-file публикация), и запускать нечем.
    /// </summary>
    [Fact]
    public void BundleWithAStrayAssemblyIsRejected()
    {
        WriteExecutable(Path.Combine(Payload, "SoundMeeter.exe"), 16);
        File.WriteAllText(Path.Combine(Payload, "leftover.dll"), "остаток сборки");

        Assert.Throws<InvalidOperationException>(() => Plan());
    }

    [Fact]
    public void MissingExecutableIsRejected()
    {
        Directory.CreateDirectory(Payload);

        Assert.Throws<InvalidOperationException>(() => Plan());
    }

    [Fact]
    public void NotAPortableExecutableIsRejected()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SoundMeeter.exe"), "это не PE-файл");

        Assert.Throws<InvalidOperationException>(() => Plan());
    }
}