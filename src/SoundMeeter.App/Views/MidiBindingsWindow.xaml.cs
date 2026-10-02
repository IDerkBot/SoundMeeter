using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
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

            // Окно создаётся уже после выбора языка, но не наследует Language от
            // родителя (у него своё визуальное дерево) — задаём явно.
            Language = LocResources.XmlLanguage;

            DataContext = new MidiBindingsViewModel(
                viewModel,
                App.ServiceProvider.GetRequiredService<IDispatcherService>());
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