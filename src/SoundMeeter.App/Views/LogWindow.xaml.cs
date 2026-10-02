using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно просмотра журнала: детализация, список файлов и «Скопировать
    /// диагностику» для обращения в поддержку (SM-A03).
    /// </summary>
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;

            var engine = App.ServiceProvider.GetRequiredService<IAudioEngine>();
            var settings = App.ServiceProvider.GetRequiredService<SettingsService>();
            var clipboard = App.ServiceProvider.GetRequiredService<IClipboardService>();
            ViewModel = new LogViewModel(engine, settings, clipboard);
            DataContext = ViewModel;
        }

        public LogViewModel ViewModel { get; }
    }
}