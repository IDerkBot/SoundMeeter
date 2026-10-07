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

        /// <summary>
        /// Настройки модуля синтеза речи (SM-E01): куда отдавать голос, каким
        /// голосом говорить, команды чата и голоса пользователей.
        /// </summary>
        private void OnTextToSpeechClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;

            var owner = Window.GetWindow(this);
            if (owner is null) return;

            // Окно одно, как и у дока: второй экземпляр с теми же настройками
            // только путает, а DataContext у него общий с главным окном.
            if (owner.OwnedWindows.OfType<TextToSpeechWindow>().FirstOrDefault() is { } existing)
            {
                if (existing.IsVisible)
                {
                    existing.Activate();
                    return;
                }
                existing.Close();
            }

new TextToSpeechWindow(new TextToSpeechSettingsViewModel(main))
        {
            Owner = owner,
        }.Show();
    }

    /// <summary>
    /// Окно подключения к Twitch (SM-F01). Отдельное окно, а не секция в настройках
    /// синтеза речи: вход в Twitch нужен и сам по себе.
    ///
    /// Повторное открытие того же окна не создаёт второго: у интеграции один вход, один
    /// набор подписок и один опрос трансляции, а второе окно запустило бы вторые — и
    /// подписка на чат упёрлась бы в лимит Twitch.
    /// </summary>
    private void OnTwitchClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;

        if (DataContext is not MainViewModel main) return;

        var owner = Window.GetWindow(this);
        if (owner is null) return;

        if (owner.OwnedWindows.OfType<TwitchWindow>().FirstOrDefault() is { } existing)
        {
            if (existing.IsVisible)
            {
                existing.Activate();
                return;
            }
            existing.Close();
        }

        new TwitchWindow(new TwitchSettingsViewModel(main, main.Twitch))
        {
            Owner = owner,
        }.Show();
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

        /// <summary>
        /// Импорт и экспорт всех настроек одним файлом (SM-C09). Диалоги выбора
        /// файла — здесь, в слое представления: Core о WPF не знает, и знать не
        /// должен (CoreWpfFreeTests). Сама работа с файлом — в VM.
        /// </summary>
        private void OnExportSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("Sm.Transfer.Export"),
                Filter = SettingsTransferService.FileFilter,
                FileName = SettingsTransferService.SuggestFileName("export"),
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };

            if (dialog.ShowDialog(owner: Window.GetWindow(this)) != true) return;

            var result = main.ExportSettings(dialog.FileName);
            MessageBox.Show(Window.GetWindow(this), result.Message, Loc.Get("Sm.Transfer.Export"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        /// <summary>
        /// Импорт заменяет настройки целиком, поэтому без подтверждения нельзя:
        /// промахнувшийся файл откатывает микшер к состоянию, которого у
        /// пользователя не было. Текущие настройки перед заменой копируются
        /// (см. MainViewModel.ImportSettings), и путь к копии показывается в ответе.
        /// </summary>
        private void OnImportSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;

            if (DataContext is not MainViewModel main) return;

            var owner = Window.GetWindow(this);
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("Sm.Transfer.Import"),
                Filter = SettingsTransferService.FileFilter,
                CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };

            if (dialog.ShowDialog(owner) != true) return;

            string fileName = System.IO.Path.GetFileName(dialog.FileName);
            var confirmed = MessageBox.Show(owner,
                Loc.Get("Sm.Transfer.Confirm", fileName),
                Loc.Get("Sm.Transfer.ConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmed != MessageBoxResult.Yes) return;

            var result = main.ImportSettings(dialog.FileName);

            if (!result.Success)
            {
                MessageBox.Show(owner, result.Message, Loc.Get("Sm.Transfer.Import"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Пустое сообщение — миграций не было; тогда показываем «импортировано».
            var message = result.Message.Length > 0 ? result.Message
                : result.BackupPath.Length > 0
                    ? Loc.Get("Sm.Transfer.ImportDoneBackup", fileName, result.BackupPath)
                    : Loc.Get("Sm.Transfer.ImportDone", fileName);

            MessageBox.Show(owner, message, Loc.Get("Sm.Transfer.Import"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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