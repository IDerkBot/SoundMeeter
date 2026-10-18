using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Audio.Contract;
using SoundMeeter.AppRouting.Contract;
using SoundMeeter.AppRouting.Contract.Implementation;
using SoundMeeter.Audio.Contract.Implementation;
using SoundMeeter.Core.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Docking.Contract;
using SoundMeeter.Docking.UseCases;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.TextToSpeech.Contract;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.UseCases;
using SoundMeeter.Update.Contract;
using SoundMeeter.Views;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Update.Contract.Implementation;
using SoundMeeter.Docking.Contract.Implementation;
using SoundMeeter.TextToSpeech.Contract.Implementation;
using SoundMeeter.TrayIcon.UseCases;
using SoundMeeter.StartUp.UseCases;
using SoundMeeter.Twitch;
using SoundMeeter.Midi;
using SoundMeeter.Infrastructure.Contract.Implementation;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Framework.Presentation;
using SoundMeeter.Infrastructure.Contract;
using SoundMeeter.Update.Presentation.ViewModels;
using MainViewModel = SoundMeeter.ViewModels.MainViewModel;

namespace SoundMeeter;

/// <summary>
/// Состав контейнера приложения.
/// </summary>
internal static class AppServices
{
    /// <summary>
    /// Состав контейнера приложения.
    ///
    /// Отдельным классом, а не телом OnStartup, ради одной проверки: отсутствующая
    /// регистрация — например, интерфейса, о котором объявлен конструктор — видна
    /// ТОЛЬКО при попытке создать объект по-настоящему. Ни сборка, ни проверки сервисов
    /// её не видят: у сервисов в проверках подменённые зависимости. Так приложение и
    /// падало при старте с «unable to resolve service», то есть первым действием
    /// пользователя.
    ///
    /// Настроек и устройств здесь не создаётся: только регистрация. Поэтому проверка
    /// состава не трогает ни файлов пользователя, ни звуковых карт.
    /// </summary>
    internal static IServiceCollection Create()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IAudioEngine, WasapiAudioEngine>();
        services.AddSingleton<SettingsService>();
        // MainViewModel/AudioService (портированы из AudioRouter) резолвят ISettingsService,
        // а настройки читаются через конкретный SettingsService — даём общий экземпляр.
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

        // Перенаправление звука приложений (SM-B02): COM-интероп Windows за интерфейсом.
        // Раньше был статическим классом и вызывался напрямую из AudioService —
        // подменить его было нечем, и проверка уходила в систему по-настоящему.
        services.AddSingleton<IAppRouter, AppRouter>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IInstalledAppsService, InstalledAppsService>();
        // Мост к UI-потоку и таймер метров: ядро знает только интерфейсы (SM-A10).
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IUiTimer>(_ => new DispatcherTimerAdapter(TimeSpan.FromMilliseconds(33)));
        // Буфер обмена (копирование диагностики и адреса дока) — тоже UI.
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IUpdateService, GithubUpdateService>();
        // Конкретный тип тоже: окно установки берёт сервис напрямую, и без этой
        // записи GetRequiredService<GithubUpdateService>() упал бы, хотя
        // интерфейс в контейнере есть. Ошибка такого рода уже случалась —
        // см. ServiceRegistrationTests: класс собирается, проверки проходят,
        // а падает только первое разрешение на старте.
        services.AddSingleton(sp => (GithubUpdateService)sp.GetRequiredService<IUpdateService>());
        services.AddSingleton<UpdateViewModel>();
        // Док-панель OBS: локальный сервер, отдающий страницу панели.
        services.AddSingleton<IObsDockServer, ObsDockServer>();
        // Правка user.ini. В контейнере, а не new() внутри VM: у установщика
        // есть конструктор с каталогом, и через него его можно проверить,
        // не трогая конфиг пользователя.
        services.AddSingleton<ObsDockInstaller>();
        services.AddSingleton<IObsDockInstaller>(sp => sp.GetRequiredService<ObsDockInstaller>());

        // Модуль синтеза речи (SM-E01). Каталог голосов и синтезатор — по
        // одному экземпляру на процесс: оба кэшируют состояние SAPI, а сам
        // модуль singleton, потому что у него очередь и поток озвучки.
        services.AddSingleton<ITtsVoiceCatalog, SapiVoiceCatalog>();
        services.AddSingleton<ITtsSpeechEngine, SapiSpeechEngine>();

        // Источник сообщений модуля озвучки: ручной ввод из окна настроек и чат
        // Twitch одновременно (CompositeTtsMessageSource сложит их). Ручной ввод
        // остаётся доступным всегда — проверить «!tts» можно и без подключения.
        services.AddSingleton<ManualTtsMessageSource>();
        // Concrete-регистрация Composite: иначе класс не резолвится по имени и
        // существует только внутри анонимной фабрики — не виден ни тестам, ни
        // Composition Root.
        //
        // Состав перечислен явно, а не через AddSingleton<T>(): у класса
        // параметр params ITtsMessageSource[], и контейнер разрешает массив как
        // IEnumerable<T> — то есть подставил бы ВСЕ источники, включая сам
        // Composite, и объявил цикл. Явный список к тому же не даёт составу
        // «поплыть» от новой регистрации источника.
        services.AddSingleton<CompositeTtsMessageSource>(sp => new CompositeTtsMessageSource(
            sp.GetRequiredService<ManualTtsMessageSource>(),
            sp.GetRequiredService<TwitchTtsMessageSource>()));
        services.AddSingleton<ITtsMessageSource>(sp => sp.GetRequiredService<CompositeTtsMessageSource>());
        services.AddSingleton<TextToSpeechService>();
        services.AddSingleton<ITtsModule>(sp => sp.GetRequiredService<TextToSpeechService>());
        services.AddSingleton<TtsViewModel>();
        // ITextToSpeechHost и ITwitchHost реализует и MainViewModel — окна
        // передают именно его, и регистрация на тот же экземпляр сохраняет
        // прежнее поведение. Цикла тут нет: сам MainViewModel эти интерфейсы
        // не запрашивает, а модули достаёт у MixerCoordinator поимённо.
        services.AddSingleton<ITextToSpeechHost>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<ITwitchHost>(sp => sp.GetRequiredService<MainViewModel>());

        // Список стрипов для MIDI, дока и озвучки — отдельный объект. Подставить
        // сюда MainViewModel было бы можно только будь он реализацией
        // IStripProvider, а он ею не является: цикл
        // MainViewModel → DockController → IStripProvider → MainViewModel
        // замкнулся бы и контейнер не собрал ни одного из них. Наполняет
        // реестр MixerCoordinator по вызову представления.
        services.AddSingleton<StripProvider>();
        services.AddSingleton<IStripProvider>(sp => sp.GetRequiredService<StripProvider>());

        // Оркестрация микшера и поведение приложения. Оба — одиночные объекты,
        // живущие столько же, сколько процесс: таймер метров, таймер сохранения и
        // подписка на движок не должны пересоздаваться вместе с окном.
        //
        // Порядок регистрации не важен для контейнера, но важен для чтения:
        // MixerCoordinator тянет DockController, TtsViewModel, TwitchViewModel и
        // MidiController, а MainViewModel — сам оркестратор.
        services.AddSingleton<AppBehaviourService>();
        services.AddSingleton<MixerCoordinator>();

        IModule[] modules =
        {
            // AudioEngine

            // AppRouting

            // Update

            // StartUp
            new StartUpModule(),
            // OBS

            // Twitch
            new TwitchModule(),
            // TTS

            // Midi
            new MidiModule(),
            // TrayIcon
            new TrayIconModule()
        };

        foreach (var module in modules)
        {
            try
            {
                module.RegisterServices(services);
            }
            catch (Exception ex)
            {
                // Логирование ошибки регистрации модуля
                Console.WriteLine($"Ошибка регистрации модуля {module.Name}: {ex.Message}");
            }
        }

        services.AddSingleton<DockController>();
        
        // Singleton: OnStartup восстанавливает пресет через этот же экземпляр,
        // что и окно (transient давал окну второй экземпляр с пустыми стрипами).
        services.AddSingleton<MainViewModel>();
        
        services.AddTransient<MainWindow>();

        return services;
    }
}
