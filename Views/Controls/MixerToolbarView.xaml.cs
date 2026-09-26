using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Панель инструментов микшера: обновление списка устройств, MIDI, обновления,
    /// старт/стоп движка и статус. DataContext — MainViewModel.
    /// </summary>
    public partial class MixerToolbarView : UserControl
    {
        public MixerToolbarView()
        {
            InitializeComponent();
        }

        /// <summary>Открыть окно привязки MIDI-контроллеров.</summary>
        private void OnMidiClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel main) return;

            var window = new MidiBindingsWindow(main)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        /// <summary>Ручная проверка обновлений: сама проверка в VM, окно открываем здесь.</summary>
        private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel main) return;
            try
            {
                await main.CheckUpdatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Проверка обновлений",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (main.PendingUpdate is { } update) MixerUi.ShowUpdateWindow(this, update);
        }
    }
}
