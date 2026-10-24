using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls;

public partial class TitleBarView : UserControl
{
    public TitleBarView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Setup window control buttons
        if (MinimizeBtn != null)
            MinimizeBtn.Click += (_, _) => GetWindow()?.WindowState = WindowState.Minimized;

        if (MaximizeBtn != null)
            MaximizeBtn.Click += (_, _) => ToggleMaximize();

        if (CloseBtn != null)
            CloseBtn.Click += (_, _) => GetWindow()?.Close();

        // Update maximize button icon based on window state
        if (GetWindow() is Window window)
        {
            window.StateChanged += (_, _) => UpdateMaximizeIcon();
            UpdateMaximizeIcon();
        }
    }

    /// <summary>
    /// Разместить TwitchStatusView в заголовке. Вызывается из MainWindow после создания VM.
    /// </summary>
    public void SetTwitchStatus(UIElement twitchStatusControl)
    {
        TwitchStatusHost.Content = twitchStatusControl;
    }

    /// <summary>
    /// Показать/скрыть кнопки обновления.
    /// </summary>
    public void SetUpdateButtonsVisible(bool visible)
    {
        UpdateButtonsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Window? GetWindow() => Window.GetWindow(this) as Window;

    private void ToggleMaximize()
    {
        var window = GetWindow();
        if (window == null) return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        var window = GetWindow();
        if (MaximizeBtn == null || window == null) return;

        MaximizeBtn.Content = window.WindowState == WindowState.Maximized ? "❐" : "☐";
    }

    #region Drag

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // Используем нативный DragMove — WindowChrome автоматически обрабатывает
        // Aero Snap: maximize при перетаскивании к верху, snap к половинам экрана и т.д.
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            GetWindow()?.DragMove();
        }
    }

    #endregion
}
