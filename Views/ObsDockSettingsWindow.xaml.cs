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
            DataContext = viewModel;
            Closed += (_, _) => viewModel.Dispose();
        }
    }
}
