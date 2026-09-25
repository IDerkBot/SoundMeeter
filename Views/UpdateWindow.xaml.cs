using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Диалог обновления: версии, changelog, прогресс загрузки и запуск установки.
    /// После передачи файлов фоновому скрипту закрывает приложение — скрипт дождётся
    /// выхода процесса и перезапустит его уже с новой сборкой.
    /// </summary>
    public partial class UpdateWindow : Window
    {
        public UpdateWindow(IUpdateService updates, UpdateInfo update)
        {
            InitializeComponent();
            ViewModel = new UpdateViewModel(updates, update);
            DataContext = ViewModel;
            ViewModel.InstallCompleted += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => Application.Current?.Shutdown()));
        }

        public UpdateViewModel ViewModel { get; }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
