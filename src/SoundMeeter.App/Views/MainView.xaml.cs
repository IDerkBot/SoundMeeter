using SoundMeeter.Services;
using System.Windows.Controls;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Корневой вид главного окна: собирает панель инструментов, ленту стрипов
    /// и панели приложений. Логика и разметка частей — в Views/Controls.
    /// DataContext — MainViewModel (задаётся окном).
    /// </summary>
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;
        }
    }
}