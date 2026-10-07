using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Services;
using SoundMeeter.Services.Twitch;
using SoundMeeter.Services.TextToSpeech;
using SoundMeeter.ViewModels;
using Xunit;

namespace SoundMeeter.App.Tests;

/// <summary>
/// Состав контейнера приложения (SM-F01).
///
/// Проверка появилась не из-за вкуса, а из-за настоящей ошибки: сервис просил
/// <see cref="ITwitchAuth"/>, а в контейнере был зарегистрирован только
/// <c>TwitchAuthService</c>. Приложение падало при старте с «unable to resolve
/// service» — то есть первым, что делал пользователь.
///
/// Почему это не ловится ничем другим. Классы собираются, проверки сервисов проходят
/// (у них подменённые зависимости), а ошибка живёт только в СПИСКЕ регистраций: тип
/// может быть в списке и одновременно быть не тем, о котором объявлен конструктор.
/// Единственная проверка, способная это увидеть, — собрать контейнер и создать
/// объекты по-настоящему.
///
/// Что именно проверяется: контейнер собирается, и все типы, которые создаются при
/// старте, создаются. Сеть при этом не трогается — ни один из них не ходит в неё в
/// конструкторе.
/// </summary>
public class ServiceRegistrationTests
{
    /// <summary>
    /// Контейнер собирается и все зависимости разрешаются.
    ///
    /// Проверяется создание объектов по-настоящему, а не просмотр списка регистраций:
    /// отсутствующая регистрация видна только при попытке создать объект.
    /// </summary>
    [Fact]
    public void TheContainerBuildsAndResolvesEverythingItIsAskedFor()
    {
        var services = AppServices.Create();

        using var provider = services.BuildServiceProvider();

        // Порядок повторяет старт приложения: сначала настройки, потом всё, что на них
        // смотрит, и только потом главная VM — она поднимает потоки и таймеры.
        Assert.NotNull(provider.GetRequiredService<SettingsService>());

        var twitch = provider.GetRequiredService<TwitchService>();
        Assert.NotNull(twitch);

        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
    }

    /// <summary>
    /// Интерфейсы указывают на те же экземпляры, что и сами классы.
    ///
    /// Отдельная регистрация интерфейса на свой экземпляр означала бы два входа в
    /// Twitch и два окна настроек с разным состоянием: одно показало бы «вошли», другое
    /// «нет», и разобраться в этом можно было бы только перезапуском.
    /// </summary>
    [Fact]
    public void TheTwitchInterfacesShareOneInstanceWithTheirClasses()
    {
        var provider = AppServices.Create().BuildServiceProvider();
        using var _ = provider;

        Assert.Same(provider.GetRequiredService<TwitchAuthService>(),
            provider.GetRequiredService<ITwitchAuth>());

        Assert.Same(provider.GetRequiredService<TwitchApiClient>(),
            provider.GetRequiredService<ITwitchApi>());

        Assert.Same(provider.GetRequiredService<TwitchChatReader>(),
            provider.GetRequiredService<ITwitchChatReader>());

        // Состояние для окна — тот же сервис, а не копия: окно обязано видеть то же
        // состояние подключения, что и приложение.
        Assert.Same(provider.GetRequiredService<TwitchService>(),
            provider.GetRequiredService<ITwitchState>());
    }

    /// <summary>
    /// Модуль озвучки получает источник, который слышит и чат, и ручной ввод.
    ///
    /// Отдельная проверка, потому что потеря ручного ввода выглядит не ошибкой, а
    /// невозможностью проверить «!tts» на новой установке, до регистрации в Twitch.
    /// </summary>
    [Fact]
    public void TheSpeechModuleGetsASourceThatIncludesBothChatAndManualInput()
    {
        var provider = AppServices.Create().BuildServiceProvider();
        using var _ = provider;

        Assert.IsType<CompositeTtsMessageSource>(provider.GetRequiredService<ITtsMessageSource>());
    }
}