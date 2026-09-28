using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            // Язык интерфейса, в т.ч. разделитель в StringFormat («0.0» / «0,0»).
            // У открытых окон ставит Loc, у созданных позже — их конструкторы.
            Language = Loc.XmlLanguage;

            ViewModel = viewModel;
            DataContext = viewModel;
            Closed += (_, _) =>
            {
                viewModel.SaveNow();
                viewModel.Shutdown();
                // Снимает подписки на Loc.LanguageChanged — окно закрылось, VM больше
                // не нужна, а держать её в списке подписчиков незачем.
                viewModel.Dispose();
            };
        }

        public MainViewModel ViewModel { get; }
    }
}