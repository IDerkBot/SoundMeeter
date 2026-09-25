using System.Globalization;
using System.Windows.Data;

namespace SoundMeeter.Converters
{
    // Конвертер для кнопки Старт/Стоп
    public class BoolToStartStopConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRunning)
            {
                return isRunning ? "Остановить" : "Запустить";
            }
            return "▶ Запустить";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
