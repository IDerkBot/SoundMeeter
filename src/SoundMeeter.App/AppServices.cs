using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Audio;
using SoundMeeter.Services;
using SoundMeeter.Services.TextToSpeech;
using SoundMeeter.Services.Twitch;
using SoundMeeter.ViewModels;
using SoundMeeter.Views;

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

            // Окно настроек получает состояние интеграции, а не сам сервис: окно
            // показывает состояние и не должно получать ещё и право им управлять.
            services.AddSingleton<ITwitchState>(sp => sp.GetRequiredService<TwitchService>());

            services.AddSingleton<TwitchTtsMessageSource>();

            // Источник сообщений модуля озвучки: ручной ввод из окна настроек и чат
            // Twitch одновременно (CompositeTtsMessageSource сложит их). Ручной ввод
            // остаётся доступным всегда — проверить «!tts» можно и без подключения.
            services.AddSingleton<ManualTtsMessageSource>();
            services.AddSingleton<ITtsMessageSource>(sp => new CompositeTtsMessageSource(
                sp.GetRequiredService<ManualTtsMessageSource>(),
                sp.GetRequiredService<TwitchTtsMessageSource>()));
            services.AddSingleton<TextToSpeechService>();

            // Singleton: OnStartup восстанавливает пресет через этот же экземпляр,
            // что и окно (transient давал окну второй экземпляр с пустыми стрипами).
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<IMidiService, MidiService>();
            services.AddTransient<MainWindow>();

            return services;
        }
}