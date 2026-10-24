using SoundMeeter.ChangeLanguage.Presentation;
using SoundMeeter.StartUp.Contract;
using SoundMeeter.TrayIcon.Contract;
using SoundMeeter.TrayIcon.Contract.Implementation;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Presentation.ViewModels;
using SoundMeeter.Twitch.Presentation.Views;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MainViewModel = SoundMeeter.ViewModels.MainViewModel;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Главное окно микшера. Держит значок в трее (SM-D02) и решает, что делать
    /// при закрытии окна.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ITrayIconService _tray;
        private TwitchStatusViewModel? _twitchStatusVm;
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

            // Инициализация title bar
            SetupTitleBar(viewModel);

            Closed += (_, _) =>
            {
                _tray.Dispose();
                _twitchStatusVm?.Dispose();
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

        /// <summary>
        /// Настроить title bar: Twitch статус, кнопки обновления, версия.
        /// </summary>
        private void SetupTitleBar(MainViewModel viewModel)
        {
            // 1. Twitch статус — берём ITwitchState из TwitchService
            var twitchState = viewModel.TwitchModule?.Twitch as ITwitchState;
            if (twitchState == null) return;
            _twitchStatusVm = new TwitchStatusViewModel(twitchState);
            var twitchView = new TwitchStatusView(_twitchStatusVm);
            TitleBar.SetTwitchStatus(twitchView);
        }

        #region WindowChrome fix: WM_GETMINMAXINFO hook

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>
        /// Подключить WndProc hook для коррекции размеров максимизированного окна.
        /// WindowStyle=None + WindowChrome растягивает окно за пределы экрана при maximize.
        /// </summary>
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            var hwndSource = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwndSource);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var mon = monitorInfo.rcMonitor;
                    // ptMaxPosition — это ОТНОСИТЕЛЬНОЕ смещение от rcMonitor,
                    // а не абсолютные координаты. Иначе для боковых мониторов
                    // окно улетает за их пределы.
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                }

                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        #endregion
    }
}
