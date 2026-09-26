using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
    }
}
