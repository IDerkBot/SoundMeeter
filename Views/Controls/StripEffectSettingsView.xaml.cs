using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Содержимое попапа параметров эффекта стрипа (ПКМ по CMP/GN/DLY/RVB в
    /// колонке кнопок). DataContext — StripEffectViewModel: список крутилок
    /// приходит данными, поэтому попап общий для всех эффектов.
    /// </summary>
    public partial class StripEffectSettingsView : UserControl
    {
        public StripEffectSettingsView()
        {
            InitializeComponent();
        }
    }
}
