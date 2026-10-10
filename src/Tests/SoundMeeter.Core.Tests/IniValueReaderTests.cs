using SoundMeeter.Docking.Contract.Implementation;
using SoundMeeter.Docking.UseCases;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Ручная правка INI в user.ini OBS.
///
/// Модуль не пользуется библиотекой INI сознательно: лишние изменения в
/// конфиге OBS недопустимы, поэтому файл пересобирается по строкам, а
/// переводы строк и порядок секций сохраняются. Именно поэтому здесь ручной
/// разбор — и именно он тихо ломает конфиг OBS, если ошибётся хоть на одном
/// символе: OBS перестаёт читать свой файл, и это выглядит как «док пропал» без
/// внятной причины.
///
/// Проверяется на тексте в памяти, а не на настоящем файле: смысл здесь в
/// разборе и склейке строк, а файловый ввод-вывод покрыт отдельно — в
/// <see cref="ObsDockConfigTests"/>.
/// </summary>
public class IniValueReaderTests
{
    private const string Section = "BasicWindow";
    private const string Key = "ExtraBrowserDocks";

    /// <summary>Файл как его пишет OBS: CRLF и секция с чужими ключами.</summary>
    private const string Sample = "[BasicWindow]\r\n" +
                                  "Geometry=1,2,3,4\r\n" +
                                  "ExtraBrowserDocks=[{ \"title\": \"A\", \"url\": \"u\", \"uuid\": \"b\" }]\r\n" +
                                  "DockState=\x01\x00\x02blob\r\n" +
                                  "[Other]\r\n" +
                                  "Geometry=9,9,9,9\r\n";

    [Fact]
    public void ReadsAValueFromTheRequestedSection()
    {
        string? value = IniValueReader.ReadValue(Sample, Section, Key);

        Assert.NotNull(value);
        Assert.Contains("\"uuid\": \"b\"", value);
    }

    [Fact]
    public void SectionAndKeyAreMatchedWithoutRegardToCase()
    {
        Assert.NotNull(IniValueReader.ReadValue(Sample, "basicwindow", "extrabrowserdocks"));
        Assert.NotNull(IniValueReader.ReadValue(Sample, "BASICWINDOW", "EXTRABROWSERDOCKS"));
    }

    /// <summary>
    /// Ключ с тем же именем в другой секции не должен подменять искомый:
    /// иначе запись попадёт не туда.
    /// </summary>
    [Fact]
    public void DoesNotLeakAValueFromAnotherSection()
    {
        Assert.Null(IniValueReader.ReadValue(Sample, "Other", Key));
        Assert.Null(IniValueReader.ReadValue(Sample, "Missing", Key));
        Assert.Null(IniValueReader.ReadValue(Sample, Section, "Missing"));
    }

    [Fact]
    public void ReadsFromAFileWithBareLineFeeds()
    {
        const string lf = "[BasicWindow]\nGeometry=1,2,3,4\nExtraBrowserDocks=[]\n";

        Assert.Equal("[]", IniValueReader.ReadValue(lf, Section, Key));
    }

    [Fact]
    public void ReplacesAnExistingValueInPlace()
    {
        string written = IniValueReader.WriteValue(Sample, Section, Key, "NEW");

        Assert.Equal("NEW", IniValueReader.ReadValue(written, Section, Key));
        Assert.DoesNotContain("\"uuid\": \"b\"", written);
    }

    /// <summary>
    /// Порядок строк внутри секции сохраняется: пересборка не должна переставлять
    /// ключи, иначе diff конфига у OBS окажется нечитаемым.
    /// </summary>
    [Fact]
    public void KeepsThePositionOfTheReplacedKey()
    {
        string written = IniValueReader.WriteValue(Sample, Section, Key, "NEW");

        int geometry = written.IndexOf("Geometry=1,2,3,4", StringComparison.Ordinal);
        int docks = written.IndexOf("ExtraBrowserDocks=NEW", StringComparison.Ordinal);
        int blob = written.IndexOf("DockState=", StringComparison.Ordinal);

        Assert.True(geometry < docks, "Geometry должен остаться перед ExtraBrowserDocks");
        Assert.True(docks < blob, "ExtraBrowserDocks должен остаться перед DockState");
    }

    /// <summary>
    /// Смесь окончаний строк в файле — то, чем OBS заканчивает дамп состояния
    /// окна: CRLF должен и дальше остаться CRLF, иначе в файле появятся
    /// неконсистентные переводы строк.
    /// </summary>
    [Fact]
    public void PreservesCarriageReturnOnTheLineItReplaces()
    {
        string written = IniValueReader.WriteValue(Sample, Section, Key, "NEW");

        Assert.Contains("ExtraBrowserDocks=NEW\r\n", written);
    }

    [Fact]
    public void PreservesBareLineFeedsWhenTheFileHadThem()
    {
        const string lf = "[BasicWindow]\nGeometry=1\nExtraBrowserDocks=[]\n";

        string written = IniValueReader.WriteValue(lf, Section, Key, "NEW");

        Assert.Contains("ExtraBrowserDocks=NEW\n", written);
        Assert.DoesNotContain("NEW\r", written);
    }

    [Fact]
    public void AddsTheKeyAtTheTopOfAnExistingSection()
    {
        const string noKey = "[BasicWindow]\r\nGeometry=1,2,3,4\r\n[Other]\r\nX=1\r\n";

        string written = IniValueReader.WriteValue(noKey, Section, Key, "NEW");

        Assert.Equal("NEW", IniValueReader.ReadValue(written, Section, Key));
        Assert.Contains("[Other]", written);
        Assert.Contains("X=1", written);

        // Ключ добавлен в начало секции — так его заметно в файле.
        int section = written.IndexOf("[BasicWindow]", StringComparison.Ordinal);
        int key = written.IndexOf("ExtraBrowserDocks=NEW", StringComparison.Ordinal);
        int geometry = written.IndexOf("Geometry=1,2,3,4", StringComparison.Ordinal);
        Assert.True(section < key && key < geometry);
    }

    [Fact]
    public void CreatesTheSectionWhenTheFileHasNone()
    {
        const string bare = "[Other]\r\nX=1\r\n";

        string written = IniValueReader.WriteValue(bare, Section, Key, "NEW");

        Assert.Equal("NEW", IniValueReader.ReadValue(written, Section, Key));
        Assert.Equal("1", IniValueReader.ReadValue(written, "Other", "X"));
    }

    /// <summary>
    /// Круговой тест: то, что записали, обязано читаться тем же кодом. Любой
    /// изъян склейки (потерянный перевод строки, ключ без «=») проявился бы
    /// здесь, а не у пользователя в конфиге OBS.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[{ \"title\": \"SoundMeeter\", \"url\": \"http://127.0.0.1:17954/\", \"uuid\": \"5c0f\" }]")]
    [InlineData("значение с кавычками \" и фигурными скобками {}")]
    public void WrittenValueIsReadableBack(string value)
    {
        string written = IniValueReader.WriteValue(Sample, Section, Key, value);

        Assert.Equal(value, IniValueReader.ReadValue(written, Section, Key));
    }

    [Fact]
    public void WritingDoesNotDisturbOtherSections()
    {
        string written = IniValueReader.WriteValue(Sample, Section, Key, "NEW");

        Assert.Equal("9,9,9,9", IniValueReader.ReadValue(written, "Other", "Geometry"));
    }

    /// <summary>Пустой файл и файл без переводов строк — не падаем.</summary>
    [Fact]
    public void HandlesDegenerateInput()
    {
        Assert.Null(IniValueReader.ReadValue("", Section, Key));

        string written = IniValueReader.WriteValue("", Section, Key, "NEW");
        Assert.Equal("NEW", IniValueReader.ReadValue(written, Section, Key));
    }
}
