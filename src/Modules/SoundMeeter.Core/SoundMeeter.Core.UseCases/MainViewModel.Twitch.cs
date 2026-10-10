using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Entities;
using SoundMeeter.Twitch.UseCases;

namespace SoundMeeter.Core.UseCases;

// Интеграция с Twitch (SM-F01) вынесена в TwitchViewModel
// (SoundMeeter.Twitch.UseCases). Этот partial оставлен для совместимости:
// все вызовы делегируются.
public partial class MainViewModel : ITwitchHost
{
    /// <summary>Доступ к VM интеграции с Twitch.</summary>
    public TwitchViewModel TwitchModule => _twitchVm;

    /// <summary>Интеграция с Twitch.</summary>
    public TwitchService Twitch => _twitchVm.Twitch;

    /// <summary>
    /// Интеграция настроена: в коде есть client ID.
    ///
    /// Пока его нет, вход невозможен, и об этом надо сказать прямо в окне — иначе
    /// кнопка «войти» молча ничего не делала бы.
    /// </summary>
    public bool IsTwitchConfigured => _twitchVm.IsTwitchConfigured;

    /// <summary>Начать вход. Возвращает код для ввода на странице Twitch либо null, если вход невозможен или сервер отказал.</summary>
    public Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default) =>
        _twitchVm.BeginTwitchLoginAsync(cancellationToken);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    public Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default) =>
        _twitchVm.ContinueTwitchLoginAsync(challenge, cancellationToken);

    /// <summary>Выйти из аккаунта.</summary>
    public Task LogoutTwitchAsync(CancellationToken cancellationToken = default) =>
        _twitchVm.LogoutTwitchAsync(cancellationToken);

    /// <summary>
    /// Применяет настройки интеграции.
    ///
    /// Отдельно от прочих настроек, потому что применение здесь имеет побочный
    /// эффект: подписка на чат создаётся заново, а канал смениться может прямо
    /// сейчас, на эфире.
    /// </summary>
    public void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true) =>
        _twitchVm.ApplyTwitchSettings(updated, saveNow);

    /// <summary>Живые настройки интеграции (лежат в SettingsService, оттуда в settings.json).</summary>
    public TwitchSettings TwitchSettings => _twitchVm.TwitchSettings;

    /// <summary>
    /// Восстановить вход при старте приложения.
    ///
    /// Порядок тот же, что у дока и модуля озвучки: настройки уже прочитаны, поэтому
    /// канал известен и подписку можно создать сразу, не дожидаясь первого
    /// открытия окна.
    /// </summary>
    public Task RestoreTwitchAsync() => _twitchVm.RestoreTwitchAsync();

    /// <summary>Открыть адрес в браузере по умолчанию.</summary>
    public void OpenTwitchConsole() => _twitchVm.OpenTwitchConsole();

    /// <summary>Открыть адрес страницы активации кода входа.</summary>
    public void OpenTwitchActivation(string verificationUri) => _twitchVm.OpenTwitchActivation(verificationUri);

    private void SubscribeTwitch() => _twitchVm.SubscribeTwitch();

    private void ShutdownTwitch() => _twitchVm.Dispose();
}
