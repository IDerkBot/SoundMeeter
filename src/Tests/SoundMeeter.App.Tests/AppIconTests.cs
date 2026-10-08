using SoundMeeter.Converters;
using SoundMeeter.Tests.Infrastructure;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Значки приложений после SM-A10 (этап 2).
///
/// До разделения значок извлекали три ViewModel'а в ядре, у каждого своя копия
/// P/Invoke в оболочку, и НИ ОДНОГО теста на это не было: код работал «на глаз»
/// и упал бы молча, если бы оболочка вернула 0 или значок оказался 32×32 вместо
/// 16×16. Теперь значком занимается UI (ShellIcons + два конвертера), и его
/// поведение проверяется — на настоящих файлах Windows.
///
/// Тесты идут через STA-хост: <c>BitmapImage</c> и <c>Imaging</c> без
/// STA-потока не работают.
/// </summary>
public class AppIconTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Заведомо существующий файл Windows со значком. <c>System32\notepad.exe</c>
    /// есть на любой машине, где вообще запускается приложение.
    /// </summary>
    private static string RealExecutable =>
        Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [Fact]
    public void FileIconConverterReturnsAnIconForARealExecutable()
    {
        var converter = new FileIconConverter();

        var icon = UiHost.Run(() => converter.Convert(RealExecutable, typeof(ImageSource), null, Culture));

        Assert.IsAssignableFrom<ImageSource>(icon);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(icon);

        // Значок должен быть пригоден для показа: ненулевой размер, и он
        // заморожен — иначе WPF попытается синхронизировать его с другим
        // потоком и упадёт в привязке.
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
            $"Значок получился пустым: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        Assert.True(bitmap.IsFrozen);
    }

    [Fact]
    public void FileIconConverterReturnsTheSameInstanceForTheSamePath()
    {
        var converter = new FileIconConverter();

        var first = UiHost.Run(() => converter.Convert(RealExecutable, typeof(ImageSource), null, Culture));
        var second = UiHost.Run(() => converter.Convert(RealExecutable, typeof(ImageSource), null, Culture));

        // Список установленных программ пересобирается на каждый символ в строке
        // поиска: без кэша конвертер дёргал бы оболочку тысячи раз.
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\нет\такого\файла.exe")]
    public void FileIconConverterReturnsNullInsteadOfThrowing(string? path)
    {
        var converter = new FileIconConverter();

        // Значок — украшение, а не данные: его отсутствие не должно ронять
        // список (раньше тот же try/catch стоял в каждом из трёх ViewModel).
        var icon = UiHost.Run(() => converter.Convert(path, typeof(ImageSource), null, Culture));

        Assert.Null(icon);
    }

    [Fact]
    public void FileIconConverterIgnoresNonPathValues()
    {
        var converter = new FileIconConverter();

        // В списке бывают элементы без пути (правило без ExecutablePath) — привязка
        // не должна падать на них.
        var icon = UiHost.Run(() => converter.Convert(42u, typeof(ImageSource), null, Culture));

        Assert.Null(icon);
    }

    [Fact]
    public void ProcessIconConverterFindsTheIconOfTheRunningTestHost()
    {
        var converter = new ProcessIconConverter();
        uint pid = (uint)Environment.ProcessId;

        var icon = UiHost.Run(() => converter.Convert(pid, typeof(ImageSource), null, Culture));

        // Тест-хост — обычный процесс Windows, значок у него есть.
        Assert.IsAssignableFrom<ImageSource>(icon);
    }

    [Fact]
    public void ProcessIconConverterReturnsNullForADeadProcess()
    {
        var converter = new ProcessIconConverter();

        // PID заведомо несуществующего процесса: приложение могло завершиться,
        // пока список перерисовывался.
        var icon = UiHost.Run(() => converter.Convert(0xFFFFFFFEu, typeof(ImageSource), null, Culture));

        Assert.Null(icon);
    }

    [Fact]
    public void IconConvertersDoNotSupportConvertBack()
    {
        var file = new FileIconConverter();
        var process = new ProcessIconConverter();

        // Привязка случайно написанная в две стороны должна упасть сразу, а не
        // молча записать null в модель.
        Assert.Throws<NotSupportedException>(
            () => file.ConvertBack(null, typeof(string), null, Culture));
        Assert.Throws<NotSupportedException>(
            () => process.ConvertBack(null, typeof(uint), null, Culture));
    }
}