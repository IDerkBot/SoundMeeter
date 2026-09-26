using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Входной стрип микшера: имя канала, VU-метр с фейдером, кнопки
    /// маршрутизации (OUT/VIRT/MONO/SOLO/MUTE/DEN) и приложения канала.
    /// DataContext — InputChannelViewModel, RemoveCommand приходит от MixerView.
    /// </summary>
    public partial class InputStripView : UserControl
    {
        public InputStripView()
        {
            InitializeComponent();
        }

        /// <summary>Команда удаления стрипа (RemoveInput из MainViewModel).</summary>
        public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
            nameof(RemoveCommand),
            typeof(ICommand),
            typeof(InputStripView),
            new PropertyMetadata(null));

        public ICommand? RemoveCommand
        {
            get => (ICommand?)GetValue(RemoveCommandProperty);
            set => SetValue(RemoveCommandProperty, value);
        }

        /// <summary>
        /// Кнопка «×». Команду берём из DP, а не из привязки RelativeSource:
        /// поиск предка внутри UserControl ненадёжен, как и ElementName до Popup.
        /// </summary>
        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not InputChannelViewModel vm) return;
            if (RemoveCommand?.CanExecute(vm.Id) == true) RemoveCommand.Execute(vm.Id);
        }

        private void OnInputNameClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not InputChannelViewModel vm) return;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            var window = new DevicePickerWindow(main.Engine.Catalog, forInput: true, allowLoopback: true)
            {
                Owner = Window.GetWindow(this)
            };
            window.SelectCurrent(vm.Model.DeviceId);
            if (window.ShowDialog() == true)
                main.Engine.SetInputSource(vm.Model.Id, window.SelectedDeviceId);
        }

        private void OnOutClick(object sender, RoutedEventArgs e) => OutPopup.IsOpen = !OutPopup.IsOpen;

        private void OnVirtClick(object sender, RoutedEventArgs e) => VirtPopup.IsOpen = !VirtPopup.IsOpen;

        /// <summary>ПКМ по DEN — открыть/закрыть попап настроек денойзера (ЛКМ — включение).</summary>
        private void OnDenRightClick(object sender, MouseButtonEventArgs e)
        {
            DenPopup.IsOpen = !DenPopup.IsOpen;
            e.Handled = true;
        }

        private void Device_DragEnter(object sender, DragEventArgs e)
        {
            var strip = DataContext as InputChannelViewModel;
            bool accepted = strip?.CanAcceptApps == true && MixerUi.GetDraggedApp(e.Data) != null &&
                            (e.AllowedEffects & DragDropEffects.Move) != 0;
            if (strip != null) strip.IsDropTarget = accepted;
            e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Device_DragLeave(object sender, DragEventArgs e)
        {
            if (DataContext is InputChannelViewModel strip) strip.IsDropTarget = false;
        }

        private async void Device_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (DataContext is not InputChannelViewModel strip) return;

            strip.IsDropTarget = false;
            var app = MixerUi.GetDraggedApp(e.Data);
            if (app == null || !strip.CanAcceptApps) return;
            e.Effects = DragDropEffects.Move;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            try
            {
                var result = await main.AssignAppToStripAsync(app, strip);
                if (!result.Success) MixerUi.ShowRoutingError(this, result.Error);
            }
            catch (Exception ex) { MixerUi.ShowRoutingError(this, ex.Message); }
        }
    }
}
