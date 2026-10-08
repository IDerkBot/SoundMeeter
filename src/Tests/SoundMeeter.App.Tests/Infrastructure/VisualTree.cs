using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Обход визуального дерева: тесты ищут элементы так же, как их видит
/// пользователь, а не по имени в разметке. Имя в XAML — это деталь исходника,
/// а «ползунок посылки существует в строке выхода» — это свойство интерфейса,
/// которое и надо защищать от регрессий.
/// </summary>
public static class VisualTree
{
    /// <summary>Все элементы типа <typeparamref name="T"/> в визуальном дереве.</summary>
    public static List<T> FindAll<T>(DependencyObject? root) where T : DependencyObject
    {
        var found = new List<T>();
        if (root is null) return found;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) found.Add(typed);
            found.AddRange(FindAll<T>(child));
        }

        return found;
    }

    /// <summary>Первый элемент типа <typeparamref name="T"/> с заданным именем.</summary>
    public static T? FindByName<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name) return found;
            var nested = FindByName<T>(child, name);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>Переключатель с заданным именем (FUNC, DEN, кнопки эффектов).</summary>
    public static ToggleButton? FindToggle(DependencyObject? root, string name) =>
        FindByName<ToggleButton>(root, name);

    /// <summary>Развернуть элемент: WPF не создаёт содержимое до измерения.</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>
    /// Два клика мышью по элементу — так WPF сообщает о двойном клике.
    /// Синтетическое событие, а не настоящая мышь: проверяется обработчик
    /// <c>ParamReset</c>, а не работа оконной системы.
    /// </summary>
    public static void DoubleClick(UIElement element)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = element
        };
        element.RaiseEvent(args);
    }

    /// <summary>
    /// Нажатие кнопки так, как это делает мышь. <c>OnClick</c> у ButtonBase —
    /// не публичный API, а вот <c>PerformClick</c> есть, но он не срабатывает у
    /// отключённой кнопки и не проходит через <c>IsChecked</c> так, как это
    /// делает реальное нажатие; поэтому зовём тот же путь, что и мышь.
    /// </summary>
    public static void Click(ToggleButton button)
    {
        var onClick = typeof(ButtonBase).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ButtonBase.OnClick не найден");

        onClick.Invoke(button, null);
    }

    /// <summary>
    /// Правая кнопка мыши: подъём события, как это делает WM_RBUTTONUP. Отдельная
    /// кнопка в интерфейсе проверяется именно так — ПКМ открывает окно настроек
    /// эквалайзера, а ЛКМ переключает эффект, и спутать их нельзя.
    /// </summary>
    public static void RightClick(UIElement element)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
            Source = element
        };
        element.RaiseEvent(args);
    }

    /// <summary>
    /// Колесо мыши: <paramref name="notches"/> щелчков вниз (отрицательное — вверх).
    /// Позиция курсора в <see cref="MouseWheelEventArgs"/> не задаётся, поэтому
    /// проверяется то, что от неё не зависит: шаг регулятора.
    /// </summary>
    public static void Wheel(UIElement element, int notches = 1)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, notches)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = element
        };
        element.RaiseEvent(args);
    }

    /// <summary>
    /// Отдельный шаблон из словаря темы в обычный визуальный элемент.
    /// У <c>ContentControl</c> своя тема, и вложенный шаблон из
    /// <c>ContentTemplate</c> не попадает в визуальное дерево так, как его
    /// видит пользователь; <c>ContentPresenter</c> — тот же приём, что и в
    /// самом интерфейсе.
    /// </summary>
    public sealed class ThemeTemplateHost : ContentPresenter
    {
        public ThemeTemplateHost(string resourceKey)
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SoundMeeter;component/Views/Controls/MixerTheme.xaml")
            };

            Resources.MergedDictionaries.Add(dictionary);
            ContentTemplate = dictionary[resourceKey] as DataTemplate
                ?? throw new InvalidOperationException($"В MixerTheme нет шаблона {resourceKey}");
        }
    }

    /// <summary>Словарь темы миксера, как его подмешивает приложение.</summary>
    public static ResourceDictionary MixerTheme() => new()
    {
        Source = new Uri("pack://application:,,,/SoundMeeter;component/Views/Controls/MixerTheme.xaml")
    };

    /// <summary>Позиция элемента относительно предка — проверка раскладки.</summary>
    public static double? Y(FrameworkElement element, FrameworkElement relativeTo) =>
        element.TranslatePoint(new System.Windows.Point(0, 0), relativeTo).Y;
}
