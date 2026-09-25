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
            ViewModel = viewModel;
            DataContext = viewModel;
            Closed += (_, _) =>
            {
                viewModel.SaveNow();
                viewModel.Shutdown();
            };
        }

        public MainViewModel ViewModel { get; }
    }
}