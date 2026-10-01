using SoundMeeter.Models;
using Xunit;

namespace SoundMeeter.Update.Tests;

/// <summary>
/// Разбор и сравнение semver (SM-E03). Отсюда берётся решение «предлагать ли
/// обновление»: ошибка в сравнении — это либо вечный баннер на свежей сборке,
/// либо тихий пропуск нового релиза.
///
/// <c>AppVersion</c> живёт в SoundMeeter.Update, потому что читает его только
/// обновление; держать его в ядре обработки звука незачем.
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("release-1.2.3", 1, 2, 3, "")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("  1.2.3  ", 1, 2, 3, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("1", 1, 0, 0, "")]
    public void ParsesTagsAsTheyComeFromGithub(string text, int major, int minor, int patch, string prerelease)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-digits-here")]
    public void RejectsGarbageInsteadOfThrowing(string text)
    {
        // Метка релиза может оказаться чем угодно: парсер не имеет права уронить
        // проверку обновлений на файле с непривычным именем.
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Fact]
    public void RejectsNullInsteadOfThrowing()
    {
        // Отдельный случай, а не [InlineData(null)]: атрибут не принимает null
        // для параметра без ссылочной аннотации, а проверить надо именно его.
        Assert.False(AppVersion.TryParse(null, out _));
    }

    [Fact]
    public void SourceLinkSuffixIsDiscarded()
    {
        // Сборка из CI даёт "1.2.3+abc1234": хеш не часть версии, иначе
        // "1.2.3+abc" и "1.2.3+def" считались бы разными релизами.
        Assert.True(AppVersion.TryParse("1.2.3+abc1234", out var version));
        Assert.Equal("1.2.3", version.ToString());
    }

    [Fact]
    public void ExtraNumericComponentIsDiscarded()
    {
        Assert.True(AppVersion.TryParse("1.2.3.9", out var version));
        Assert.Equal("1.2.3", version.ToString());
    }

    [Fact]
    public void NewerPatchIsNewerRelease()
    {
        Assert.True(new AppVersion(1, 2, 3) > new AppVersion(1, 2, 2));
        Assert.True(new AppVersion(1, 3, 0) > new AppVersion(1, 2, 9));
        Assert.True(new AppVersion(2, 0, 0) > new AppVersion(1, 9, 9));
    }

    [Fact]
    public void ReleaseIsNewerThanItsOwnPrerelease()
    {
        // Правило semver, и оно тут неочевидно: без него сборка 1.2.3-beta
        // считалась бы новее финальной 1.2.3, и релиз не предлагался бы.
        Assert.True(new AppVersion(1, 2, 3) > new AppVersion(1, 2, 3, "beta.1"));
        Assert.True(new AppVersion(1, 2, 3, "beta.1") < new AppVersion(1, 2, 3));
    }

    [Fact]
    public void PrereleasesCompareBySuffix()
    {
        Assert.True(new AppVersion(1, 0, 0, "beta.2") > new AppVersion(1, 0, 0, "beta.1"));
    }

    [Fact]
    public void EqualVersionsCompareEqualRegardlessOfInstance()
    {
        Assert.Equal(new AppVersion(1, 2, 3), new AppVersion(1, 2, 3));
        Assert.True(new AppVersion(1, 2, 3) == new AppVersion(1, 2, 3));
        Assert.Equal(new AppVersion(1, 2, 3).GetHashCode(), new AppVersion(1, 2, 3).GetHashCode());
    }

    [Fact]
    public void NullIsOlderThanAnyVersion()
    {
        Assert.True(new AppVersion(1, 0, 0) > null);
    }
}
