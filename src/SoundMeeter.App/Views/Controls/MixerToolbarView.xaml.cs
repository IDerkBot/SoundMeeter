using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Панель инструментов микшера: кнопка настроек (устройства, MIDI, док OBS,
    /// обновления, журнал), старт/стоп движка и статус. DataContext — MainViewModel.
    /// </summary>
    public partial class MixerToolbarView : UserControl
    {
        public MixerToolbarView()
        {
            InitializeComponent();
        }

        /// <summary>Показать/скрыть выпадающее меню настроек.</summary>
        private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

        /// <summary>Пункт, выполняющийся командой: только закрываем меню.</summary>
        private void OnMenuItemClick(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = false;

        /// <summary>Открыть окно привязки MIDI-контроллеров.</summary>
        private void OnMidiClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;

            var window = new MidiBindingsWindow(main)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        /// <summary>Настройки док-панели в OBS: каналы, порт, установка дока.</summary>
        private void OnObsDockClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;

            var owner = Window.GetWindow(this);
            if (owner is null) return;

            // Окно одно: второй экземпляр с теми же настройками только путает.
            if (owner.OwnedWindows.OfType<ObsDockSettingsWindow>().FirstOrDefault() is { } existing)
            {
                if (existing.IsVisible)
                {
                    existing.Activate();
                    return;
                }
                existing.Close();
            }

            new ObsDockSettingsWindow(new ObsDockSettingsViewModel(
                main,
                App.ServiceProvider.GetRequiredService<IClipboardService>()))
            { Owner = owner }.Show();
        }

        /// <summary>Окно просмотра журнала и копирования диагностики.</summary>
        private void OnLogsClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            var owner = Window.GetWindow(this);
            if (owner is null) return;

            // Окно одно на приложение: держать второй просмотрщик незачем, а
            // DataContext у него общий с главным окном.
            if (owner.OwnedWindows.OfType<LogWindow>().FirstOrDefault() is { } existing)
            {
                if (existing.IsVisible)
                {
                    existing.Activate();
                    return;
                }
                existing.Close();
            }

            new LogWindow { Owner = owner }.Show();
        }

        /// <summary>Ручная проверка обновлений: сама проверка в VM, окно открываем здесь.</summary>
        private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;
            try
            {
                await main.CheckUpdatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.Get("Sm.Toolbar.CheckUpdatesFailed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (main.PendingUpdate is { } update) MixerUi.ShowUpdateWindow(this, update);
        }
    }
}