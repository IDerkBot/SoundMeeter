using SoundMeeter.Controls;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Выходная шина (стрип вывода): имя, VU-метр с фейдером, MONO/SOLO/MUTE.
    /// DataContext — OutputBusViewModel, RemoveCommand приходит от MixerView.
    /// </summary>
    public partial class OutputStripView : UserControl
    {
        public OutputStripView()
        {
            InitializeComponent();
        }

        /// <summary>Команда удаления стрипа (RemoveBus из MainViewModel).</summary>
        public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
            nameof(RemoveCommand),
            typeof(ICommand),
            typeof(OutputStripView),
            new PropertyMetadata(null));

        public ICommand? RemoveCommand
        {
            get => (ICommand?)GetValue(RemoveCommandProperty);
            set => SetValue(RemoveCommandProperty, value);
        }

        /// <summary>
        /// Кнопка «×». Команду берём из DP, а не из привязки RelativeSource:
        /// поиск предка внутри UserControl ненадёжен.
        /// </summary>
        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not OutputBusViewModel vm) return;
            if (RemoveCommand?.CanExecute(vm.Model.Id) == true) RemoveCommand.Execute(vm.Model.Id);
        }

        private void OnBusNameClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not OutputBusViewModel vm) return;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            var window = new DevicePickerWindow(main.Engine.Catalog, forInput: false)
            {
                Owner = Window.GetWindow(this)
            };
            window.SelectCurrent(vm.Model.DeviceId);
            if (window.ShowDialog() == true)
                main.Engine.SetBusSource(vm.Model.Id, window.SelectedDeviceId);
        }

        #region Перетаскивание стрипа

        /// <summary>
        /// ЛКМ с протяжкой по имени канала — перетаскивание стрипа (SM-C06).
        /// Список берётся у ленты, которая и показывает линию вставки.
        /// </summary>
        private void OnStripDragStart(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not OutputBusViewModel vm || vm.IsRenaming) return;

            FindStripPanel(this)?.BeginDrag(e.GetPosition(this));
        }

        private void OnStripDragMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not OutputBusViewModel vm) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var panel = FindStripPanel(this);
            if (panel is null || !panel.AllowReorder) return;
            if (!panel.DragThresholdReached(e.GetPosition(this))) return;

            var data = new DataObject(HorizontalFillPanel.StripDragFormat, vm.Model.Id);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }

        private static HorizontalFillPanel? FindStripPanel(DependencyObject? start)
        {
            for (var current = start; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is HorizontalFillPanel panel) return panel;
            }

            return null;
        }

        #endregion
    }
}
