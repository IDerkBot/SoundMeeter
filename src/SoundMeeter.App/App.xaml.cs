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

        private SingleInstanceGuard? _instance;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Журнал поднимаем первым: иначе ранние ошибки (настройки, каталог
            // устройств) снова останутся без следа — ровно та проблема, ради
            // которой логирование и заводилось (SM-A03).
            AppLog.Initialize(Microsoft.Extensions.Logging.LogLevel.Information);
            

            // Один экземпляр (SM-D03). Проверка идёт до всего остального: второй
            // процесс не должен ни создавать окна, ни трогать устройства.
            // Заодно просим первый экземпляр показать окно — с треем иначе
            // повторный клик по ярлыку выглядел бы как «ничего не произошло».
            _instance = new SingleInstanceGuard();
            if (_instance.AlreadyRunning)
            {
                SingleInstanceGuard.RequestShowWindow();
                AppLog.Shutdown();
                Shutdown();
                return;
            }

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
            // Мост к UI-потоку и таймер метров: ядро знает только интерфейсы (SM-A10).
            services.AddSingleton<IDispatcherService, DispatcherService>();
            services.AddSingleton<IUiTimer>(_ => new DispatcherTimerAdapter(TimeSpan.FromMilliseconds(33)));
            // Буфер обмена (копирование диагностики и адреса дока) — тоже UI.
            services.AddSingleton<IClipboardService, ClipboardService>();
            services.AddSingleton<IUpdateService, GithubUpdateService>();
            // Автозапуск вместе с Windows (SM-D01).
            services.AddSingleton<IStartupService, StartupService>();
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

            // Настройки поведения приложения: трей и автозапуск (SM-D01/SM-D02).
            // Читаются до показа окна, чтобы иконка в трее появилась сразу, а не
            // мигала через секунду после старта.
            viewModel.RestoreAppBehaviour();

            // Сначала восстанавливаем пресет (оба списка стрипов), затем перечитываем
            // каталог: так стрипы получают актуальную доступность по устройствам.
            // Порядок наоборот был бы тем же результатом — движок сам стрипов
            // не создаёт ни при каких обстоятельствах.
            viewModel.Restore(saved);
            engine.RefreshDevices();

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

            // Повторный запуск выводит окно из трея (SM-D03).
            _instance?.ListenForShowRequest(() => Dispatcher.Invoke(mainWindow.ShowFromTray));

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
            try
            {
                // Мьютекс отпускается последним: до этого момента второй экземпляр
                // ещё может считать первый работающим, что и нужно.
                _instance?.Dispose();
            }
            catch
            {
                // Игнорируем ошибки освобождения одиночного экземпляра.
            }
            base.OnExit(e);
        }
    }
}