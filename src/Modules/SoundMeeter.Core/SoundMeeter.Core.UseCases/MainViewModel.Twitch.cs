using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;
using SoundMeeter.Services.Twitch;

namespace SoundMeeter.ViewModels;

// Мост между интеграцией с Twitch (SM-F01) и интерфейсом. Сам сервис живёт в
// SoundMeeter.Services.Twitch и ничего не знает про главную VM; здесь только
// подписки, жизненный цикл и то, что окно настроек видит как «настройки Twitch».
//
// Отдельный partial по той же причине, что MainViewModel.ObsDock и
// MainViewModel.Midi: файл главной VM уже держит много тем, и ещё одна задача в его
// конструкторе ничего не добавит.
public partial class MainViewModel : ITwitchHost
{
    private readonly TwitchService _twitch;
    private readonly ILogger _twitchLogger = AppLog.For<MainViewModel>();

    /// <summary>Интеграция с Twitch.</summary>
    public TwitchService Twitch => _twitch;

    /// <summary>Живые настройки интеграции (лежат в SettingsService, оттуда в settings.json).</summary>
    public TwitchSettings TwitchSettings => _settings.Settings.Twitch;

    /// <summary>
    /// Интеграция настроена: в коде есть client ID.
    ///
    /// Пока его нет, вход невозможен, и об этом надо сказать прямо в окне — иначе
    /// кнопка «войти» молча ничего не делала бы.
    /// </summary>
    public bool IsTwitchConfigured => TwitchService.IsConfigured;

    /// <summary>
    /// Начать вход. Возвращает код для ввода на странице Twitch либо null, если вход
    /// невозможен или сервер отказал.
    /// </summary>
    public Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default) =>
        _twitch.BeginLoginAsync(cancellationToken);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    public Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default) =>
        _twitch.ContinueLoginAsync(challenge, cancellationToken);

    /// <summary>Выйти из аккаунта.</summary>
    public Task LogoutTwitchAsync(CancellationToken cancellationToken = default) =>
        _twitch.LogoutAsync(cancellationToken);

    /// <summary>
    /// Применить настройки интеграции.
    ///
    /// Отдельно от прочих настроек, потому что применение здесь имеет побочный
    /// эффект: подписка на чат создаётся заново, а канал смениться может прямо
    /// сейчас, на эфире.
    /// </summary>
    public void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true)
    {
        var live = TwitchSettings;

        live.Enabled = updated.Enabled;
        live.ChannelLogin = updated.ChannelLogin;
        live.ShowStreamStatus = updated.ShowStreamStatus;

        if (saveNow) _settings.Save();

        _twitch.ApplySettings();
    }

    /// <summary>
    /// Восстановить вход при старте приложения.
    ///
    /// Порядок тот же, что у дока и модуля озвучки: настройки уже прочитаны, поэтому
    /// канал известен и подписку можно создать сразу, не дожидаясь первого
    /// открытия окна.
    /// </summary>
    public async Task RestoreTwitchAsync()
    {
        try
        {
            await _twitch.RestoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Восстановление входа не должно мешать запуску микшера: приложение
            // обязано открыться и работать даже без Twitch.
            _twitchLogger.LogWarning(ex, "Twitch: восстановление входа не удалось: {Message}", ex.Message);
        }
    }

    /// <summary>Открыть адрес в браузере по умолчанию.</summary>
    public void OpenTwitchConsole()
    {
        // Страница регистрации приложения: без неё вход настроить нечем.
        TwitchUrlOpener.Open("https://twitch.tv/console");
    }

    /// <summary>Открыть адрес страницы активации кода входа.</summary>
    public void OpenTwitchActivation(string verificationUri)
    {
        // Адрес присылает сам Twitch. Проверка схемы обязательна: без неё в разметку
        // или в сообщение могла бы попасть произвольная строка, а её открытие —
        // это запуск чего угодно от имени пользователя.
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;

        TwitchUrlOpener.Open(uri.ToString());
    }

    private void SubscribeTwitch()
    {
        _twitch.StatusChanged += OnTwitchStatusChanged;

        // Восстановление входа — фоновая операция: сеть может не ответить, а микшер
        // обязан открыться сразу.
        _ = RestoreTwitchAsync();
    }

    private void OnTwitchStatusChanged() =>
        _twitchLogger.LogDebug("Twitch: {State}", TwitchStatusText);

    /// <summary>
    /// Текст состояния интеграции одной строкой — для журнала.
    ///
    /// Подробности живут в окне настроек, а здесь достаточно понять, что было в
    /// момент события.
    /// </summary>
    private string TwitchStatusText =>
        $"вход: {(_twitch.IsAuthorized ? _twitch.AuthorizedLogin : "нет")}, " +
        $"чат: {(_twitch.IsChatConnected ? "подключён" : "нет")}, " +
        $"трансляция: {_twitch.StreamState switch
        {
            TwitchStreamState.Live => "идёт",
            TwitchStreamState.Offline => "не идёт",
            _ => "неизвестно",
        }}";

    /// <summary>
    /// Открытие адреса в браузере.
    ///
    /// Отдельный класс с заменяемым действием, а не вызов Process.Start прямо в VM:
    /// открытие окна браузера в проверке означало бы, что тест действительно что-то
    /// запускает.
    /// </summary>
    internal static class TwitchUrlOpener
    {
        /// <summary>Действие открытия. Заменяется в проверках.</summary>
        public static Action<string> Open { get; set; } = DefaultOpen;

        private static void DefaultOpen(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                           or System.IO.IOException
                                           or InvalidOperationException
                                           or NotSupportedException)
            {
                // Отсутствие браузера — не повод ронять приложение: пользователь может
                // открыть адрес сам, а вот необработанное исключение уронило бы
                // приложение посреди работы.
                AppLog.For<MainViewModel>()
                    .LogWarning(ex, "Не удалось открыть адрес «{Url}»: {Message}", url, ex.Message);
            }
        }
    }

    private void ShutdownTwitch()
    {
        _twitch.StatusChanged -= OnTwitchStatusChanged;

        // Освобождение останавливает поток чтения чата и поток опроса трансляции.
        // Порядок тот же, что у дока и модуля озвучки: сначала отписка, потом
        // освобождение.
        _twitch.Dispose();
    }
}