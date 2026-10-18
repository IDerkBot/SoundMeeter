using SoundMeeter.ChangeLanguage.Presentation;
using SoundMeeter.Audio.Contract;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Infrastructure.Contract.Implementation;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace SoundMeeter.Logger.Presentation.Views
{
    /// <summary>
    /// Окно просмотра журнала: детализация, список файлов и «Скопировать
    /// диагностику» для обращения в поддержку (SM-A03).
    /// </summary>
    public partial class LogWindow : Window
    {
        public LogWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
            Language = LocResources.XmlLanguage;

            var engine = serviceProvider.GetRequiredService<IAudioEngine>();
            var settings = serviceProvider.GetRequiredService<SettingsService>();
            var clipboard = serviceProvider.GetRequiredService<IClipboardService>();
            ViewModel = new LogViewModel(engine, settings, clipboard);
            DataContext = ViewModel;
        }

        public LogViewModel ViewModel { get; }
    }
}