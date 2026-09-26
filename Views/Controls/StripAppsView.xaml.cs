using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Список приложений канала: активные (AssignedApps) и ожидающие правила
    /// (ConfiguredApps). Карточки перетаскиваются на другие стрипы.
    /// DataContext — InputChannelViewModel.
    /// </summary>
    public partial class StripAppsView : UserControl
    {
        private readonly AppDragSource _drag = new();

        public StripAppsView()
        {
            InitializeComponent();
        }

        private void App_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _drag.Begin(this, e);

        private void App_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element) _drag.Move(this, element, e);
        }

        private async void RemoveStripApp_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _drag.Cancel();
            if (sender is not FrameworkElement element) return;
            if (MixerUi.FindMainViewModel(this) is not { } main) return;

            try
            {
                var result = await main.RemoveStripAppAsync(element.DataContext);
                if (!result.Success) MixerUi.ShowRoutingError(this, result.Error);
            }
            catch (Exception ex) { MixerUi.ShowRoutingError(this, ex.Message); }
        }
    }
}
