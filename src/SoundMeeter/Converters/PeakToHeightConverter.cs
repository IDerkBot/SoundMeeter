using System.Globalization;
using System.Windows.Data;

namespace SoundMeeter.Converters
{
    /// <summary>
    /// Конвертирует float (PeakLevel) в высоту вертикального VU-метра в пикселях.
    /// Использует ту же логарифмическую шкалу, что и PeakToWidthConverter.
    /// </summary>
    public class PeakToHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float peak && double.TryParse(parameter?.ToString(), out double maxHeight))
            {
                if (peak <= 0.0001f) return 0.0;

                double db = 20.0 * Math.Log10(peak);
                double normalized = (db + 60.0) / 66.0;
                normalized = Math.Max(0.0, Math.Min(1.0, normalized));

                return Math.Max(1.0, normalized * maxHeight);
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}