using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls;

/// <summary>
/// Attached behavior для кастомного resize окна по всем 8 направлениям.
/// Вешается на корневой Grid окна: находит все дочерние Border с Tag="Resize_*"
/// и подключает логику изменения размера.
///
/// Handle-зоны задаются прямо в XAML MainWindow (Grid 3×3) — так разметка
/// остаётся декларативной, а behavior только добавляет обработку мыши.
/// </summary>
public static class WindowChromeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WindowChromeBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    private static Window? _activeWindow;
    private static Point _resizeStart;
    private static double _startWidth, _startHeight, _startLeft, _startTop;
    private static bool _isResizing;
    private static ResizeDirection _currentDirection;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;

        if ((bool)e.NewValue)
        {
            grid.Loaded += OnGridLoaded;
        }
        else
        {
            grid.Loaded -= OnGridLoaded;
        }
    }

    private static void OnGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid) return;

        _activeWindow = Window.GetWindow(grid);

        foreach (var child in grid.Children)
        {
            if (child is not Border border) continue;
            if (border.Tag is not string tag || !tag.StartsWith("Resize_")) continue;

            if (Enum.TryParse<ResizeDirection>(tag.Replace("Resize_", ""), out var direction))
            {
                border.Cursor = GetCursor(direction);
                border.MouseLeftButtonDown += HandleMouseDown;
                border.MouseMove += HandleMouseMove;
                border.MouseLeftButtonUp += HandleMouseUp;
                border.MouseLeave += HandleMouseLeave;
            }
        }
    }

    private static void HandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string tag) return;
        if (!Enum.TryParse<ResizeDirection>(tag.Replace("Resize_", ""), out var direction)) return;
        if (_activeWindow == null || _activeWindow.WindowState == WindowState.Maximized) return;

        _isResizing = true;
        _currentDirection = direction;
        _resizeStart = e.GetPosition(_activeWindow);
        _startWidth = _activeWindow.Width;
        _startHeight = _activeWindow.Height;
        _startLeft = _activeWindow.Left;
        _startTop = _activeWindow.Top;

        border.CaptureMouse();
        e.Handled = true;
    }

    private static void HandleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing || _activeWindow == null) return;

        var pos = e.GetPosition(_activeWindow);
        var dx = pos.X - _resizeStart.X;
        var dy = pos.Y - _resizeStart.Y;
        var minW = _activeWindow.MinWidth;
        var minH = _activeWindow.MinHeight;

        switch (_currentDirection)
        {
            case ResizeDirection.Right:
                _activeWindow.Width = Math.Max(minW, _startWidth + dx);
                break;

            case ResizeDirection.Left:
                if (_startWidth - dx >= minW)
                {
                    _activeWindow.Width = _startWidth - dx;
                    _activeWindow.Left = _startLeft + dx;
                }
                break;

            case ResizeDirection.Bottom:
                _activeWindow.Height = Math.Max(minH, _startHeight + dy);
                break;

            case ResizeDirection.Top:
                if (_startHeight - dy >= minH)
                {
                    _activeWindow.Height = _startHeight - dy;
                    _activeWindow.Top = _startTop + dy;
                }
                break;

            case ResizeDirection.TopLeft:
                if (_startWidth - dx >= minW)
                {
                    _activeWindow.Width = _startWidth - dx;
                    _activeWindow.Left = _startLeft + dx;
                }
                if (_startHeight - dy >= minH)
                {
                    _activeWindow.Height = _startHeight - dy;
                    _activeWindow.Top = _startTop + dy;
                }
                break;

            case ResizeDirection.TopRight:
                if (_startWidth + dx >= minW)
                    _activeWindow.Width = _startWidth + dx;
                if (_startHeight - dy >= minH)
                {
                    _activeWindow.Height = _startHeight - dy;
                    _activeWindow.Top = _startTop + dy;
                }
                break;

            case ResizeDirection.BottomLeft:
                if (_startWidth - dx >= minW)
                {
                    _activeWindow.Width = _startWidth - dx;
                    _activeWindow.Left = _startLeft + dx;
                }
                if (_startHeight + dy >= minH)
                    _activeWindow.Height = _startHeight + dy;
                break;

            case ResizeDirection.BottomRight:
                _activeWindow.Width = Math.Max(minW, _startWidth + dx);
                _activeWindow.Height = Math.Max(minH, _startHeight + dy);
                break;
        }

        e.Handled = true;
    }

    private static void HandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isResizing = false;
        if (sender is Border border) border.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static void HandleMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isResizing && sender is Border border) border.ReleaseMouseCapture();
    }

    private static Cursor GetCursor(ResizeDirection direction) => direction switch
    {
        ResizeDirection.Top or ResizeDirection.Bottom => Cursors.SizeNS,
        ResizeDirection.Left or ResizeDirection.Right => Cursors.SizeWE,
        ResizeDirection.TopLeft or ResizeDirection.BottomRight => Cursors.SizeNWSE,
        ResizeDirection.TopRight or ResizeDirection.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow
    };

    private enum ResizeDirection
    {
        Top, Bottom, Left, Right,
        TopLeft, TopRight, BottomLeft, BottomRight
    }
}
