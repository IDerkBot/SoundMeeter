using SoundMeeter.Controls;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Входной стрип микшера: имя канала, VU-метр с фейдером, кнопки
    /// маршрутизации (OUT/VIRT/MONO/SOLO/MUTE/DEN/FUNC1/FUNC2) и приложения канала.
    /// DataContext — InputChannelViewModel, RemoveCommand приходит от MixerView.
    /// </summary>
    public partial class InputStripView : UserControl
    {
        public InputStripView()
        {
            InitializeComponent();

            // Применили или сняли правило — закрываем попап: результат виден по
            // нажатой кнопке и по метрам, а не за стеной попапа.
            Func1View.CloseRequested += (_, _) => Func1Popup.IsOpen = false;
            Func2View.CloseRequested += (_, _) => Func2Popup.IsOpen = false;
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

        /// <summary>
        /// Выбор устройства, в которое уходят приложения канала. Нужен там, где
        /// связку кабеля угадать нельзя: имя кабеля задаёт пользователь, а Windows
        /// признака второй половинки не отдаёт.
        /// </summary>
        private void OnAppTargetClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not InputChannelViewModel vm) return;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            var window = new DevicePickerWindow(main.Engine.Catalog, forInput: false)
            {
                Owner = Window.GetWindow(this)
            };
            window.SelectCurrent(vm.AppSourceDeviceId);
            if (window.ShowDialog() == true)
                main.Engine.SetInputAppTarget(vm.Id, window.SelectedDeviceId);
        }

        private void OnOutClick(object sender, RoutedEventArgs e) => OutPopup.IsOpen = !OutPopup.IsOpen;

        #region Перетаскивание стрипа

        private HorizontalFillPanel? StripPanel => FindPanel(this);

        /// <summary>Лента стрипов, которой принадлежит этот стрип.</summary>
        private static HorizontalFillPanel? FindPanel(DependencyObject? start)
        {
            for (var current = start; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is HorizontalFillPanel panel) return panel;
            }

            return null;
        }

        /// <summary>
        /// ЛКМ по имени канала: начинаем возможное перетаскивание. Само перетаскивание
        /// стартует только после того, как указатель прошёл системный порог — иначе
        /// простой щелчок по имени тянул бы за собой полосы.
        /// </summary>
        private void OnStripDragStart(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not InputChannelViewModel strip) return;
            if (strip.IsRenaming) return;

            StripPanel?.BeginDrag(e.GetPosition(this));
        }

        private void OnStripDragMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not InputChannelViewModel strip) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var panel = StripPanel;
            if (panel is null || !panel.AllowReorder) return;
            if (!panel.DragThresholdReached(e.GetPosition(this))) return;

            // В данных лежит Id модели канала, а не его позиция: список мог
            // измениться (устройство подключили) прямо во время перетаскивания,
            // и панель найдёт источник по Id сама.
            var data = new DataObject(HorizontalFillPanel.StripDragFormat, strip.Model.Id);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }

        #endregion

        private void OnVirtClick(object sender, RoutedEventArgs e) => VirtPopup.IsOpen = !VirtPopup.IsOpen;

        /// <summary>ПКМ по DEN — открыть/закрыть попап настроек денойзера (ЛКМ — включение).</summary>
        private void OnDenRightClick(object sender, MouseButtonEventArgs e)
        {
            DenPopup.IsOpen = !DenPopup.IsOpen;
            e.Handled = true;
        }

        #region Кнопки FUNC

        // Само переключение делает ToggleButton (двусторонняя привязка IsChecked),
        // а ПКМ открывает назначение. Попап опознаётся по кнопке: обе кнопки
        // живут в одной колонке, а их контекст — FuncButtonViewModel, а не стрип.

        private void OnFuncRightClick(object sender, MouseButtonEventArgs e)
        {
            if (FuncPopupOf(sender as FrameworkElement) is { } popup) popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        }

        /// <summary>
        /// Нажатие кнопки без назначения открывает попап назначения, а не
        /// выглядит сломанной кнопкой: назначать выходы иначе негде — попап
        /// открывается только по самой кнопке.
        ///
        /// Само переключение к этому моменту уже откатилось: пустое назначение
        /// владелец стрипа отверг и поднял IsEngaged, поэтому кнопка осталась
        /// не нажатой.
        /// </summary>
        private void OnFuncClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement button) return;
            if (button.DataContext is not FuncButtonViewModel { CanApply: false }) return;
            if (FuncPopupOf(button) is { } popup) popup.IsOpen = true;
        }

        private Popup? FuncPopupOf(FrameworkElement? button) =>
            ReferenceEquals(button, Func1Btn) ? Func1Popup
            : ReferenceEquals(button, Func2Btn) ? Func2Popup
            : null;

        #endregion

        private void Device_DragEnter(object sender, DragEventArgs e)
        {
            var strip = DataContext as InputChannelViewModel;

            // Перетаскивание самого стрипа — не наше дело: его ловит лента
            // (HorizontalFillPanel). Здесь обязательно оставляем событие
            // необработанным, иначе панель до перетаскивания не доберётся.
            if (MixerUi.GetDraggedApp(e.Data) == null) return;

            bool accepted = strip?.CanAcceptApps == true &&
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
            if (DataContext is not InputChannelViewModel strip) return;

            strip.IsDropTarget = false;

            // Не приложение — значит перетаскивают стрип, это ловит лента.
            var app = MixerUi.GetDraggedApp(e.Data);
            if (app == null) return;

            e.Handled = true;
            e.Effects = DragDropEffects.Move;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            // Стрип без render-источника приложений не берём и его назначение не
            // меняем: показываем, куда можно, вместо молчаливого игнорирования.
            if (!strip.CanAcceptApps)
            {
                MixerUi.ShowRoutingError(this, strip.AppRejectReason());
                return;
            }

            try
            {
                var result = await main.AssignAppToStripAsync(app, strip);
                if (!result.Success) MixerUi.ShowRoutingError(this, result.Error);
            }
            catch (Exception ex) { MixerUi.ShowRoutingError(this, ex.Message); }
        }
    }
}
