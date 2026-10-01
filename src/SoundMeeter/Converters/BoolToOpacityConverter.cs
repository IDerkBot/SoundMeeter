using System.Globalization;
using System.Windows.Data;

namespace SoundMeeter.Converters
{
    /// <summary>
    /// true → 1.0 (полностью виден), false → 0.4 (устройство недоступно).
    /// </summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? 1.0 : 0.4;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}