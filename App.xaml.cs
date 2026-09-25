using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using SoundMeeter.Views;
using System.Windows;

namespace SoundMeeter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            services.AddSingleton<IAudioEngine, WasapiAudioEngine>();
            services.AddSingleton<SettingsService>();
            // MainViewModel/AudioService (портированы из AudioRouter) резолвят ISettingsService,
            // а настройки читаются через конкретный SettingsService — даём общий экземпляр.
            services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

            services.AddSingleton<IAudioService, AudioService>();
            services.AddSingleton<IInstalledAppsService, InstalledAppsService>();
            services.AddSingleton<IDispatcherService, DispatcherService>();
            services.AddSingleton<IUpdateService, UpdateService>();

            // Singleton: OnStartup восстанавливает пресет через этот же экземпляр,
            // что и окно (transient давал окну второй экземпляр с пустыми стрипами).
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<IMidiService, MidiService>();
            services.AddTransient<MainWindow>();
            ServiceProvider = services.BuildServiceProvider();

            var settingsService = ServiceProvider.GetRequiredService<SettingsService>();
            var engine = ServiceProvider.GetRequiredService<IAudioEngine>();
            var saved = await settingsService.LoadAsync();
            settingsService.Settings = saved ?? new Models.AppSettings();
            var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();

            // Сначала восстанавливаем пресет: возвращаем оба списка стрипов И
            // набор «скрытых» устройств (см. ApplyPreset). Только затем
            // перечитываем каталог, чтобы AdoptDevicesUnlocked не создал
            // заново стрипы для устройств, скрытых в прошлом запуске.
            viewModel.Restore(saved);
            engine.RefreshDevices();

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Тихо проверяем GitHub Releases после показа окна: без модальных окон,
            // при наличии обновления в интерфейсе появится баннер.
            _ = viewModel.CheckUpdatesOnStartupAsync();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Гарантированно освобождаем NAudio-устройства и DI-контейнер,
            // чтобы процесс завершился без зависания.
            try
            {
                if (ServiceProvider is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
                // Игнорируем ошибки завершения.
            }
            base.OnExit(e);
        }
    }
}
