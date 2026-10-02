using System.Globalization;
using System.Windows.Data;
using SoundMeeter.Services;

namespace SoundMeeter.Converters
{
    /// <summary>
    /// Значок запущенного процесса по PID (SM-A10).
    ///
    /// Раньше <c>AppViewModel</c> сам дёргал <c>SHGetFileInfo</c> в своём
    /// конструкторе и отдавал <c>ImageSource</c>, то есть значок рисовался до того,
    /// как строка вообще появилась в списке. Теперь конвертер спрашивает значок у
    /// UI-стороны по <c>ProcessId</c>, а список приложений в ядре не знает, что
    /// значки бывают.
    /// </summary>
    public class ProcessIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is uint processId ? ShellIcons.FromProcess(processId) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("ProcessIconConverter работает только в одну сторону.");
    }
}