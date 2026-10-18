using SoundMeeter.ChangeLanguage.Presentation;
using System.Windows;
using SoundMeeter.TextToSpeech.Presentation;

namespace SoundMeeter.TextToSpeech.Presentation.Views
{
    /// <summary>
    /// Окно настроек модуля синтеза речи (SM-E01): куда отдавать голос, каким
    /// голосом говорить и как разбирать реплики чата.
    /// DataContext — <see cref="TextToSpeechSettingsViewModel"/>.
    /// </summary>
    public partial class TextToSpeechWindow : Window
    {
        public TextToSpeechWindow(TextToSpeechSettingsViewModel viewModel)
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;
            DataContext = viewModel;
            Closed += (_, _) => viewModel.Dispose();
        }

        /// <summary>
        /// Закрытие окна. IsCancel="True" в разметке отвечает только за ESC и сам
        /// по себе не закрывает окно по клику — кнопке нужен обработчик, как в
        /// ObsDockSettingsWindow и MidiBindingsWindow.
        /// </summary>
        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
