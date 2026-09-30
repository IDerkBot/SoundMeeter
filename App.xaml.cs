using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;
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

            // Журнал поднимаем первым: иначе ранние ошибки (настройки, каталог
            // устройств) снова останутся без следа — ровно та проблема, ради
            // которой логирование и заводилось (SM-A03).
            AppLog.Initialize(Microsoft.Extensions.Logging.LogLevel.Information);

            // Язык — до всего, что создаёт окна: словарь строк нужен разметке уже
            // при разборе первого XAML. Значение берём у системы и уточняем ниже,
            // как только прочитаны настройки.
            //
            // Именно LocResources, а не Loc: подключение словаря — это WPF
            // (ResourceDictionary в Application.Resources). Сам Loc живёт в
            // SoundMeeter.Audio и про UI ничего не знает.
            LocResources.Install(this);

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
            // Док-панель OBS: локальный сервер, отдающий страницу панели.
            services.AddSingleton<IObsDockServer, ObsDockServer>();

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

            // Уровень журнала — тоже настройка: подхватываем сохранённый.
            AppLog.SetLevel(AppLog.ParseLevel(settingsService.Settings.LogLevel));

            // Язык интерфейса (SM-C07). Поле необязательное, поэтому у файлов,
            // написанных прошлыми сборками, оно пустое — это «язык системы».
            Loc.SetLanguage(settingsService.Settings.Language);

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
            try
            {
                AppLog.Shutdown();
            }
            catch
            {
                // Игнорируем ошибки закрытия журнала.
            }
            base.OnExit(e);
        }
    }
}