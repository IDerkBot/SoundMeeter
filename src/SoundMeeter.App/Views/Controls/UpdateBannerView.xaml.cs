using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Баннер доступного обновления. Показывается поверх панели инструментов,
    /// пока в MainViewModel ждёт найденный релиз. DataContext — MainViewModel.
    /// </summary>
    public partial class UpdateBannerView : UserControl
    {
        public UpdateBannerView()
        {
            InitializeComponent();
        }

        /// <summary>Кнопка «Update» в баннере.</summary>
        private void OnUpdateInstallClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { PendingUpdate: { } update })
                MixerUi.ShowUpdateWindow(this, update);
        }
    }
}
