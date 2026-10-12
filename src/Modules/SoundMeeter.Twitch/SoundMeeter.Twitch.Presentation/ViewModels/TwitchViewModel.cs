using Microsoft.Extensions.Logging;
using SoundMeeter.Core.Contract;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Presentation.ViewModels;

/// <summary>
/// VM модуля Twitch (SM-F01): мост между <see cref="TwitchService"/> и
/// интерфейсом. Сам сервис ничего не знает про главную VM; здесь подписки,
/// жизненный цикл, вход/выход и то, что окно настроек видит как «настройки Twitch».
/// </summary>
public sealed class TwitchViewModel : ITwitchHost, IDisposable
{
    private readonly TwitchService _twitch;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger = AppLog.For<TwitchViewModel>();

    /// <summary>Интеграция с Twitch.</summary>
    public TwitchService Twitch => _twitch;

    public TwitchViewModel(TwitchService twitch, ISettingsService settings)
    {
        _twitch = twitch;
        _settings = settings;
        _twitch.StatusChanged += OnTwitchStatusChanged;
    }

    /// <summary>
    /// Интеграция настроена: в коде есть client ID.
    ///
    /// Пока его нет, вход невозможен, и об этом надо сказать прямо в окне — иначе
    /// кнопка «войти» молча ничего не делала бы.
    /// </summary>
    public bool IsTwitchConfigured => TwitchService.IsConfigured;

    /// <summary>Начать вход. Возвращает код для ввода на странице Twitch либо null, если вход невозможен или сервер отказал.</summary>
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
    /// Применяет настройки интеграции.
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

    /// <summary>Живые настройки интеграции (лежат в SettingsService, оттуда в settings.json).</summary>
    public TwitchSettings TwitchSettings => _settings.Settings.Twitch;

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
            _logger.LogWarning(ex, "Twitch: восстановление входа не удалось: {Message}", ex.Message);
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

    /// <summary>
    /// Подписка на статус: вход и подключение чата восстанавливаются сразу при
    /// старте, а не по открытию окна.
    /// </summary>
    public void SubscribeTwitch() => _ = RestoreTwitchAsync();

    /// <summary>
    /// Статус интеграции изменился. Подробности живут в окне настроек, а здесь
    /// достаточно отметки в журнале.
    /// </summary>
    private void OnTwitchStatusChanged() =>
        _logger.LogDebug("Twitch: {State}", TwitchStatusText);

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
    /// Освобождение останавливает поток чтения чата и поток опроса трансляции.
    /// Порядок тот же, что у дока и модуля озвучки: сначала отписка, потом
    /// освобождение.
    /// </summary>
    public void Dispose()
    {
        _twitch.StatusChanged -= OnTwitchStatusChanged;
        _twitch.Dispose();
    }
}

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
            AppLog.For<TwitchViewModel>()
                .LogWarning(ex, "Не удалось открыть адрес «{Url}»: {Message}", url, ex.Message);
        }
    }
}
