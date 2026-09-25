using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Логика взаимодействия для MainView.xaml
    /// </summary>
    public partial class MainView : UserControl
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainView()
        {
            InitializeComponent();
        }

        /// <summary>ПКМ по имени канала — начать редактирование имени.</summary>
        private void OnStripTitleRightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            switch (fe.DataContext)
            {
                case InputChannelViewModel { IsRenaming: false } input:
                    input.BeginRename();
                    break;
                case OutputBusViewModel { IsRenaming: false } bus:
                    bus.BeginRename();
                    break;
            }
        }

        /// <summary>TextBox появился (IsRenaming=true) — фокус и выделить имя.</summary>
        private void OnRenameBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && sender is TextBox box)
            {
                box.Focus();
                box.SelectAll();
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

        private void OnRouteButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Popup popup })
                popup.IsOpen = !popup.IsOpen;
        }

        /// <summary>ПКМ по DEN — открыть/закрыть попап настроек денойзера (ЛКМ — включение).</summary>
        private void OnDenRightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Popup popup })
                popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        }

        private void OnInputNameClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: InputChannelViewModel vm }) return;
            var main = DataContext as MainViewModel;
            if (main == null) return;

            var window = new DevicePickerWindow(main.Engine.Catalog, forInput: true, allowLoopback: true)
            {
                Owner = Window.GetWindow(this)
            };
            window.SelectCurrent(vm.Model.DeviceId);
            if (window.ShowDialog() == true)
                main.Engine.SetInputSource(vm.Model.Id, window.SelectedDeviceId);
        }

        private void OnBusNameClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: OutputBusViewModel vm }) return;
            var main = DataContext as MainViewModel;
            if (main == null) return;

            var window = new DevicePickerWindow(main.Engine.Catalog, forInput: false)
            {
                Owner = Window.GetWindow(this)
            };
            window.SelectCurrent(vm.Model.DeviceId);
            if (window.ShowDialog() == true)
                main.Engine.SetBusSource(vm.Model.Id, window.SelectedDeviceId);
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

            if (main.PendingUpdate is { } update) ShowUpdateWindow(update);
        }

        /// <summary>Кнопка «Update» в баннере.</summary>
        private void OnUpdateInstallClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel main && main.PendingUpdate is { } update) ShowUpdateWindow(update);
        }

        private void ShowUpdateWindow(SoundMeeter.Models.UpdateInfo update)
        {
            var updates = App.ServiceProvider.GetRequiredService<IUpdateService>();
            var window = new UpdateWindow(updates, update)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private void App_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart == null ||
                sender is not FrameworkElement element) return;
            var delta = e.GetPosition(this) - _dragStart.Value;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragStart = null;
            var app = element.DataContext;
            if (app is not (AppViewModel or InstalledAppViewModel or ConfiguredAppViewModel)) return;
            if (app is InstalledAppViewModel installed && string.IsNullOrWhiteSpace(installed.ExecutablePath)) return;
            try
            {
                DragDrop.DoDragDrop(element, new DataObject(app.GetType().Name, app), DragDropEffects.Move);
            }
            finally
            {
                foreach (var strip in ViewModel.Inputs) strip.IsDropTarget = false;
            }
        }

        private Point? _dragStart;

        private void App_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(this);
        }

        private static object? GetDraggedApp(IDataObject data) =>
            data.GetData("AppViewModel") ?? data.GetData("InstalledAppViewModel") ?? data.GetData("ConfiguredAppViewModel");

        private void Device_DragEnter(object sender, DragEventArgs e)
        {
            var strip = (sender as FrameworkElement)?.DataContext as InputChannelViewModel;
            bool accepted = strip?.CanAcceptApps == true && GetDraggedApp(e.Data) != null &&
                            (e.AllowedEffects & DragDropEffects.Move) != 0;
            if (strip != null) strip.IsDropTarget = accepted;
            e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Device_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is InputChannelViewModel device)
            {
                device.IsDropTarget = false;
            }
        }

        private async void Device_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (sender is not FrameworkElement { DataContext: InputChannelViewModel strip }) return;
            strip.IsDropTarget = false;
            var app = GetDraggedApp(e.Data);
            if (app == null || !strip.CanAcceptApps) return;
            e.Effects = DragDropEffects.Move;
            try
            {
                var result = await ViewModel.AssignAppToStripAsync(app, strip);
                if (!result.Success) ShowRoutingError(result.Error);
            }
            catch (Exception ex) { ShowRoutingError(ex.Message); }
        }

        private async void RemoveStripApp_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _dragStart = null;
            if (sender is not FrameworkElement element) return;
            try
            {
                var result = await ViewModel.RemoveStripAppAsync(element.DataContext);
                if (!result.Success) ShowRoutingError(result.Error);
            }
            catch (Exception ex) { ShowRoutingError(ex.Message); }
        }

        private void ShowRoutingError(string error) => MessageBox.Show(Window.GetWindow(this), error,
            "Перенаправление звука", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
