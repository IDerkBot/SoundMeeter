using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Панель «Приложения с аудио»: запущенные приложения, карточки которых
    /// перетаскиваются на стрипы. DataContext — MainViewModel.
    /// </summary>
    public partial class RunningAppsView : UserControl
    {
        private readonly AppDragSource _drag = new();

        public RunningAppsView()
        {
            InitializeComponent();
        }

        private void App_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _drag.Begin(this, e);

        private void App_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element) _drag.Move(this, element, e);
        }
    }
}
