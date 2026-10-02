using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно настроек док-панели OBS: сервер, порт, список каналов дока и
    /// установка самого дока в OBS. DataContext — ObsDockSettingsViewModel.
    /// </summary>
    public partial class ObsDockSettingsWindow : Window
    {
        public ObsDockSettingsWindow(ObsDockSettingsViewModel viewModel)
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;
            DataContext = viewModel;
            Closed += (_, _) => viewModel.Dispose();
        }

        /// <summary>
        /// Закрытие окна. IsCancel="True" в разметке отвечает только за ESC и
        /// сам по себе не закрывает окно по клику — кнопке нужен обработчик,
        /// как в MidiBindingsWindow и UpdateWindow.
        /// </summary>
        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}