using SoundMeeter.Docking.Contract.Implementation;
using SoundMeeter.Docking.UseCases;
using System.Text;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Правка списка доков OBS в user.ini (SM-F01).
///
/// Это единственное место в приложении, где SoundMeeter пишет в конфиг чужой
/// программы. Ошибка здесь стоит пользователю его доков, поэтому проверяется на
/// настоящем файле во временной папке — так же, как проверяется работа с
/// settings.json: важно не разобрать JSON, а реально не испортить чужой файл.
///
/// Чего здесь сознательно нет: проверки отказа при запущенном OBS. Она упирается
/// в список процессов, то есть результат зависит от машины, где тест поставили.
/// Сама защита проверяется тем, что она стоит в <see cref="ObsDockInstaller"/>,
/// а не в коде правки, — обойти её нельзя, не сломав класс.
/// </summary>
public sealed class ObsDockConfigTests : IDisposable
{
    private const string Section = "BasicWindow";
    private const string Key = "ExtraBrowserDocks";

    private readonly string _directory;
    private readonly string _path;

    public ObsDockConfigTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SoundMeeterDockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "user.ini");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Проверка не должна падать из-за того, что файл ещё держит антивирус.
        }
    }

    /// <summary>Файл как его пишет OBS: CRLF, наш док отсутствует, ключи чужие.</summary>
    private void WriteUserIni(string docks = "")
    {
        File.WriteAllText(_path,
            "[BasicWindow]\r\n" +
            "Geometry=10,20,300,400\r\n" +
            (docks.Length > 0 ? Key + "=" + docks + "\r\n" : "") +
            "DockState=\x01\x00blob\r\n" +
            "[Other]\r\n" +
            "Language=ru-RU\r\n",
            new UTF8Encoding(false));
    }

    private string Docks() => IniValueReader.ReadValue(File.ReadAllText(_path), Section, Key) ?? "";

    [Fact]
    public void InstallingAddsTheDockWithOurFixedUuid()
    {
        WriteUserIni();

        var result = ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");

        Assert.True(result.Success);
        Assert.True(ObsDockConfig.IsDockInstalled(_path));

        string docks = Docks();
        Assert.Contains(ObsDockConfig.DockUuid, docks);
        Assert.Contains("http://127.0.0.1:17954/", docks);
    }

    /// <summary>
    /// Повторная установка обновляет адрес, а не добавляет второй док: иначе
    /// каждый сменённый порт оставлял бы пользователю дубль в меню View → Docks,
    /// который штатно удалить нельзя.
    /// </summary>
    [Fact]
    public void InstallingTwiceUpdatesTheUrlInsteadOfDuplicating()
    {
        WriteUserIni();
        ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");

        var second = ObsDockConfig.Install(_path, "http://127.0.0.1:17960/");

        Assert.True(second.Success);

        string docks = Docks();
        Assert.Contains("17960", docks);
        Assert.DoesNotContain("17954", docks);

        // Запись ровно одна — считаем по нашему uuid.
        Assert.Equal(1, CountOccurrences(docks, ObsDockConfig.DockUuid));
    }

    /// <summary>Чужие доки пользователя не трогаем: это его OBS, не наш.</summary>
    [Fact]
    public void UninstallingLeavesForeignDocksAlone()
    {
        WriteUserIni("[{ \"title\": \"Панель теста\", \"url\": \"http://example.test/\", \"uuid\": \"deadbeef\" }]");

        Assert.True(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/").Success);
        var result = ObsDockConfig.Uninstall(_path);

        Assert.True(result.Success);
        Assert.False(ObsDockConfig.IsDockInstalled(_path));

        string docks = Docks();
        Assert.Contains("deadbeef", docks);
        Assert.Contains("Панель теста", docks);
    }

    [Fact]
    public void UninstallingWhenOurDockIsAbsentReportsNothingToRemove()
    {
        WriteUserIni("[{ \"title\": \"Панель теста\", \"url\": \"http://example.test/\", \"uuid\": \"deadbeef\" }]");

        var result = ObsDockConfig.Uninstall(_path);

        Assert.True(result.Success);
        Assert.Contains("deadbeef", Docks());
    }

    /// <summary>
    /// Битый JSON — это «не трогаем файл»: OBS напишет свой заново при выходе
    /// и затрёт нашу запись, но и мы не затрём его настройки, разбирая то, что
    /// не поняли.
    /// </summary>
    [Fact]
    public void MalformedJsonIsRefusedAndTheFileIsLeftAlone()
    {
        // Без концевых пробелов: значение ключа при чтении обрезается, и сравнение
        // с исходником иначе проверяло бы не разбор, а Trim.
        const string broken = "[{ \"title\": \"A\",";
        WriteUserIni(broken);
        string before = File.ReadAllText(_path);

        var install = ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");

        Assert.False(install.Success);
        Assert.Equal(broken, IniValueReader.ReadValue(File.ReadAllText(_path), Section, Key));
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void JsonThatIsNotAnArrayIsRefused()
    {
        WriteUserIni("{\"title\": \"не список\"}");

        Assert.False(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/").Success);
    }

    /// <summary>
    /// Ключ не массив — та же история: разобрать нечего, а затереть нельзя.
    /// </summary>
    [Fact]
    public void DocksKeyThatIsNotDocksIsLeftAlone()
    {
        WriteUserIni("какой-то мусор");

        Assert.False(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/").Success);
        Assert.Equal("какой-то мусор", Docks());
    }

    [Fact]
    public void MissingFileIsReportedRatherThanCreated()
    {
        var result = ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");

        Assert.False(result.Success);
        Assert.False(File.Exists(_path));
    }

    /// <summary>
    /// Резервная копия делается один раз за сеанс OBS: повторная правка не должна
    /// затирать копию исходного состояния.
    /// </summary>
    [Fact]
    public void BackupKeepsTheVeryFirstStateAndIsNotRefreshed()
    {
        WriteUserIni();
        string original = File.ReadAllText(_path);
        string backup = _path + ".soundmeeter.bak";

        ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");
        Assert.True(File.Exists(backup));
        Assert.Equal(original, File.ReadAllText(backup));

        // Вторая правка меняет адрес, но копия остаётся прежней.
        ObsDockConfig.Install(_path, "http://127.0.0.1:17960/");
        Assert.Equal(original, File.ReadAllText(backup));
    }

    /// <summary>
    /// Чужие настройки OBS переживают правку: и в других секциях, и в своей.
    /// Проверка на это отдельная от проверки доков, потому что ломается тут
    /// именно пересборка файла, а не разбор JSON.
    /// </summary>
    [Fact]
    public void ForeignSettingsSurviveTheRewrite()
    {
        WriteUserIni();

        ObsDockConfig.Install(_path, "http://127.0.0.1:17954/");
        string after = File.ReadAllText(_path);

        Assert.Equal("10,20,300,400", IniValueReader.ReadValue(after, Section, "Geometry"));
        Assert.Equal("ru-RU", IniValueReader.ReadValue(after, "Other", "Language"));
        Assert.Contains("DockState=\x01\x00blob", after);
    }

    /// <summary>
    /// Список доков собирается вручную, поэтому значение приходится экранировать
    /// само. Кавычка в URL на практике не встречается, но проверка тут не про
    /// практику: она закрепляет, что <c>Escape</c> вообще работает. Убери его —
    /// значение разорвёт JSON, и следующая же правка его уже не разберёт, то есть
    /// док перестанет обновляться после первой установки.
    /// </summary>
    [Fact]
    public void QuoteInTheValueDoesNotBreakTheJson()
    {
        WriteUserIni();

        Assert.True(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/?t=\"скрипт\"").Success);

        // Ключевая проверка: то, что мы записали, обязано снова разбираться.
        var second = ObsDockConfig.Install(_path, "http://127.0.0.1:17955/");

        Assert.True(second.Success);
        Assert.Contains("17955", Docks());
        Assert.DoesNotContain("17954", Docks());
    }

    /// <summary>Вопросительная строка с &amp; и = — обычное дело для адреса дока.</summary>
    [Fact]
    public void QueryStringSurvivesTheRewrite()
    {
        WriteUserIni();

        Assert.True(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/?theme=dark&fps=30").Success);
        Assert.True(ObsDockConfig.Install(_path, "http://127.0.0.1:17955/?theme=dark&fps=30").Success);

        Assert.Contains("fps=30", Docks());
        Assert.Contains("17955", Docks());
    }

    /// <summary>Пустой список доков (ключа ещё нет) — не ошибка, а обычное состояние.</summary>
    [Fact]
    public void InstallingIntoAFileWithoutTheDocksKeyWorks()
    {
        WriteUserIni();

        Assert.True(ObsDockConfig.IsDockInstalled(_path) == false);
        Assert.True(ObsDockConfig.Install(_path, "http://127.0.0.1:17954/").Success);
        Assert.True(ObsDockConfig.IsDockInstalled(_path));
    }

    [Fact]
    public void IsDockInstalledIsFalseWhenThereIsNoFile()
    {
        Assert.False(ObsDockConfig.IsDockInstalled(_path));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
