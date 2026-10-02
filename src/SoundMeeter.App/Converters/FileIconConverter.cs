using System.Globalization;
using System.Windows.Data;
using SoundMeeter.Services;

namespace SoundMeeter.Converters
{
    /// <summary>
    /// Значок файла/программы по пути (SM-A10). Заменяет <c>StringToImageSourceConverter</c>,
    /// который умел только <c>.ico</c> по прямому пути и не вызывался ни разу.
    ///
    /// Значок извлекает UI, а не ViewModel: <c>BitmapSource</c> — это WPF, и в ядре
    /// ему не место (см. <c>SoundMeeter.Core.csproj</c>).
    /// </summary>
    public class FileIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string path ? ShellIcons.FromFile(path) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("FileIconConverter работает только в одну сторону.");
    }
}