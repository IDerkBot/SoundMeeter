using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Содержимое попапа настроек денойзера (ПКМ по DEN на входном стрипе).
    /// Открывается из InputStripView. DataContext — InputChannelViewModel.
    /// </summary>
    public partial class DenoiserSettingsView : UserControl
    {
        public DenoiserSettingsView()
        {
            InitializeComponent();
        }
    }
}
