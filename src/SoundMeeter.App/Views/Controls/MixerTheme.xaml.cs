using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Общие стили, шаблоны и обработчики элементов микшера.
    /// Обработчики правки имени подключены к общим DataTemplate через EventSetter
    /// (в MixerTheme.xaml), потому что внутри шаблона нельзя указать имя
    /// обработчика из XAML. Раньше всё это лежало в ресурсах самого MainView.
    /// </summary>
    public partial class MixerTheme
    {
        /// <summary>ПКМ по имени канала — начать редактирование имени.</summary>
        private void OnStripTitleRightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            switch (fe.DataContext)
            {
                case InputChannelViewModel { IsRenaming: false } input:
                    input.BeginRename();
                    FocusRenameBox(fe);
                    break;
                case OutputBusViewModel { IsRenaming: false } bus:
                    bus.BeginRename();
                    FocusRenameBox(fe);
                    break;
            }
        }

        /// <summary>
        /// Поле правки имени — TextBox внутри поверхности заголовка. Фокус ставим
        /// сразу после BeginRename: IsVisibleChanged — обычное CLR-событие,
        /// на него нельзя повесить EventSetter.
        /// </summary>
        private static void FocusRenameBox(FrameworkElement surface)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(surface); i++)
            {
                if (VisualTreeHelper.GetChild(surface, i) is not TextBox box) continue;
                box.Focus();
                box.SelectAll();
                return;
            }
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            switch (box.DataContext)
            {
                case InputChannelViewModel input:
                    if (e.Key == Key.Enter) input.CommitRename();
                    else if (e.Key == Key.Escape) input.CancelRename();
                    break;
                case OutputBusViewModel bus:
                    if (e.Key == Key.Enter) bus.CommitRename();
                    else if (e.Key == Key.Escape) bus.CancelRename();
                    break;
            }
        }

        /// <summary>Потеря фокуса (клик мимо) — зафиксировать имя, если ещё редактируем.</summary>
        private void OnRenameLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;

            if (box.DataContext is InputChannelViewModel { IsRenaming: true } input)
                input.CommitRename();
            else if (box.DataContext is OutputBusViewModel { IsRenaming: true } bus)
                bus.CommitRename();
        }
    }
}
