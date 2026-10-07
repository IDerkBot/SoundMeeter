using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно подключения к Twitch (SM-F01): вход, канал для чтения чата и показ
    /// статуса трансляции.
    ///
    /// Модесное (Show, а не ShowDialog): вход занимает время — код вводится на
    /// странице Twitch в браузере, — и модальное окно держало бы поверх себя всё
    /// приложение, пока пользователь там ходит.
    ///
    /// DataContext — <see cref="TwitchSettingsViewModel"/>.
    /// </summary>
    public partial class TwitchWindow : Window
    {
        public TwitchWindow(TwitchSettingsViewModel viewModel)
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;
            DataContext = viewModel;
            Closed += (_, _) => viewModel.Dispose();
        }

        /// <summary>
        /// Закрытие кнопкой. IsCancel="True" и обработчик Close — по образцу
        /// TextToSpeechWindow: единая привычка закрывать окно, а не расхождение
        /// с остальными окнами приложения.
        /// </summary>
        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}