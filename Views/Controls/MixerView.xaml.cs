using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Лента стрипов: группы INPUTS и OUTPUTS с кнопками добавления.
    /// DataContext — MainViewModel: команды добавления и удаления стрипов,
    /// а также RemoveCommand для стрипов берутся отсюда.
    /// </summary>
    public partial class MixerView : UserControl
    {
        public MixerView()
        {
            InitializeComponent();
        }
    }
}
