using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Audio.Contract;
using SoundMeeter.AppRouting.Contract;
using SoundMeeter.AppRouting.Contract.Implementation;
using SoundMeeter.Audio.Contract.Implementation;
using SoundMeeter.Core.Contract;
using SoundMeeter.Core.UseCases;
using SoundMeeter.Docking.Contract;
using SoundMeeter.Docking.UseCases;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.Infrastructure.Clipboard;
using SoundMeeter.Infrastructure.Dispatcher;
using SoundMeeter.Infrastructure.Settings;
using SoundMeeter.Infrastructure.UiTimer;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Contract.Implementation;
using SoundMeeter.Midi.Presentation.ViewModels;
using SoundMeeter.Midi.UseCases;
using SoundMeeter.TextToSpeech.Contract;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.StartUp.Contract;
using SoundMeeter.StartUp.UseCases;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.UseCases;
using SoundMeeter.Update.Contract;
using SoundMeeter.Update.UseCases;
using SoundMeeter.Update.Presentation.ViewModels;
using SoundMeeter.Views;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Update.Contract.Implementation;
using SoundMeeter.Docking.Contract.Implementation;
using SoundMeeter.TextToSpeech.Contract.Implementation;

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
        // Автозапуск вместе с Windows (SM-D01).
        services.AddSingleton<IStartupService, StartupService>();
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

        // Интеграция с Twitch (SM-F01). Порядок важен: токены и клиент API живут
        // по одному экземпляру на процесс, потому что у них кэш и состояние входа,
        // а сервис поднимает подписку на чат и опрос трансляции — и это тоже
        // единственный в процессе.
        services.AddSingleton<ITwitchTokenStore, TwitchTokenFileStore>();
        services.AddSingleton(_ => new HttpClient());

        // Сервис просит ИНТЕРФЕЙСЫ, а не конкретные классы: так его можно проверить
        // с подменой без сети. Поэтому каждый интерфейс указывает на тот же
        // экземпляр, что и сам класс, — регистрация через GetRequiredService.
        // Регистрация класса без интерфейса дала бы «unable to resolve service».
        services.AddSingleton<TwitchAuthService>();
        services.AddSingleton<ITwitchAuth>(sp => sp.GetRequiredService<TwitchAuthService>());

        services.AddSingleton<TwitchApiClient>();
        services.AddSingleton<ITwitchApi>(sp => sp.GetRequiredService<TwitchApiClient>());

        services.AddSingleton<TwitchChatReader>();
        services.AddSingleton<ITwitchChatReader>(sp => sp.GetRequiredService<TwitchChatReader>());

        services.AddSingleton<TwitchService>();
        services.AddSingleton<TwitchViewModel>();

        // Окно настроек получает состояние интеграции, а не сам сервис: окно
        // показывает состояние и не должно получать ещё и право им управлять.
        services.AddSingleton<ITwitchState>(sp => sp.GetRequiredService<TwitchService>());

        services.AddSingleton<TwitchTtsMessageSource>();

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
        // не запрашивает, а получает модули конкретными типами.
        services.AddSingleton<ITextToSpeechHost>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<ITwitchHost>(sp => sp.GetRequiredService<MainViewModel>());

        // Список стрипов для MIDI, дока и озвучки — отдельный объект, а не
        // MainViewModel: тот сам реализует IStripProvider, но подставить его в
        // контейнер значило бы замкнуть цикл MainViewModel → DockViewModel →
        // IStripProvider → MainViewModel и упасть на старте. Наполняет его
        // MainViewModel при каждой пересборке стрипов.
        services.AddSingleton<StripProvider>();
        services.AddSingleton<IStripProvider>(sp => sp.GetRequiredService<StripProvider>());

        // Singleton: OnStartup восстанавливает пресет через этот же экземпляр,
        // что и окно (transient давал окну второй экземпляр с пустыми стрипами).
        services.AddSingleton<MainViewModel>();

        IModule[] modules =
        {
            new MidiModule()
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
        services.AddTransient<MainWindow>();

        return services;
    }
}
