using System.Globalization;
using System.Windows.Data;

namespace SoundMeeter.Converters
{
    /// <summary>
    /// Конвертирует float (PeakLevel от 0.0 до 1.0) в ширину VU-метра в пикселях.
    /// Использует логарифмическую шкалу (как в профессиональных VU-метрах).
    /// </summary>
    public class PeakToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float peak && double.TryParse(parameter?.ToString(), out double maxWidth))
            {
                // peak обычно от 0.0 до 1.0
                // Логарифмическая шкала: 20 * log10(peak) дает dB
                // Преобразуем в диапазон 0..1 для визуализации

                if (peak <= 0.0001f) return 0.0; // Тишина

                // Логарифмическое преобразование (имитация dB-шкалы)
                // -60 dB = 0.001 (почти тишина), 0 dB = 1.0 (максимум)
                double db = 20.0 * Math.Log10(peak);

                // Нормализуем в диапазон 0..1 (от -60 dB до +6 dB)
                double normalized = (db + 60.0) / 66.0; // 66 = 60 - (-6)
                normalized = Math.Max(0.0, Math.Min(1.0, normalized));

                return normalized * maxWidth;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
