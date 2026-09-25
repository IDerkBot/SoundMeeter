using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно привязки MIDI-контроллеров к параметрам стрипов (фейдеры, кнопки, крутилки).
    /// </summary>
    public partial class MidiBindingsWindow : Window
    {
        public MidiBindingsWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = new MidiBindingsViewModel(viewModel);
            Closed += (_, _) =>
            {
                if (DataContext is MidiBindingsViewModel vm)
                    vm.Dispose();
            };
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}