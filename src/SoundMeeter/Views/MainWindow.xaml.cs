using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Главное окно микшера. Держит значок в трее (SM-D02) и решает, что делать
    /// при закрытии окна.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly TrayIconService _tray;
        private bool _reallyClosing;

        public MainWindow(MainViewModel viewModel, IStartupService startup)
        {
            InitializeComponent();

            // Числа в подписи ("0.0" / "0,0") зависят от языка элемента, поэтому
            // задаём его явно, а не наследуем от приложения: окно создаётся позже
            // первой установки языка.
            Language = LocResources.XmlLanguage;

            ViewModel = viewModel;
            DataContext = viewModel;

            _tray = new TrayIconService(
                "SoundMeeter",
                ShowFromTray,
                () => Dispatcher.Invoke(() => viewModel.ToggleRun()),
                ExitForReal);

            // Настройки трея живут во ViewModel, окно только применяет их:
            // VM не знает про Window, а окно — про NotifyIcon.
            viewModel.OnApplyTraySettings = () => Dispatcher.Invoke(ApplyTraySettings);
            viewModel.NotifyTray = (title, message) => Dispatcher.Invoke(() => _tray.ShowBalloon(title, message));
            ApplyTraySettings();

            Closed += (_, _) =>
            {
                _tray.Dispose();
                viewModel.OnApplyTraySettings = null;
                viewModel.NotifyTray = null;
                viewModel.SaveNow();
                viewModel.Shutdown();
                // Порядок важен: после Shutdown словарь Loc закрывается, и
                // уведомление об ошибке уже не напечатается — VM освобождается
                // последним.
                viewModel.Dispose();
            };
        }

        public MainViewModel ViewModel { get; }

        /// <summary>Показать окно из трея: развернуть и поднять поверх остальных.</summary>
        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            Activate();
            Topmost = true;
            Topmost = false;   // одно сбрасывание снимает «поверх всех»
            Focus();
        }

        /// <summary>
        /// Закрытие окна: при включённом трее это сворачивание, а не выход.
        /// Явный выход остаётся в меню трея — иначе приложение было бы невозможно
        /// закрыть, не выгружая его в диспетчере задач.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_reallyClosing && ViewModel.TrayEnabled)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        private void ExitForReal()
        {
            _reallyClosing = true;
            Close();
        }

        private void ApplyTraySettings()
        {
            _tray.IsEnabled = ViewModel.TrayEnabled;
            _tray.SuppressClose = ViewModel.TrayEnabled;
            _tray.Refresh();

            // Окно скрыто, но трей выключили: возвращаем пользователя в интерфейс,
            // иначе он останется с невидимым приложением без иконки.
            if (!ViewModel.TrayEnabled && !IsVisible) ShowFromTray();
        }
    }
}