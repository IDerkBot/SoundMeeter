using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Twitch.Entities;
using SoundMeeter.Twitch.Presentation.ViewModels;
using SoundMeeter.Twitch.UseCases;


namespace SoundMeeter.Framework.Presentation.ViewModels;

// Интеграция с Twitch (SM-F01) вынесена в TwitchViewModel
// (SoundMeeter.Twitch.UseCases). Этот partial оставлен для совместимости:
// все вызовы делегируются. Сам модуль теперь принадлежит оркестратору —
// подписка на чат и опрос трансляции живут столько же, сколько живёт
// приложение, а не столько же, сколько открыто главное окно.
public partial class MainViewModel
{
    private TwitchViewModel TwitchVm => _coordinator.Twitch;

    /// <summary>Доступ к VM интеграции с Twitch.</summary>
    public TwitchViewModel TwitchModule => TwitchVm;

    /// <summary>Интеграция с Twitch.</summary>
    public TwitchService Twitch => TwitchVm.Twitch;

    /// <summary>
    /// Интеграция настроена: в коде есть client ID.
    ///
    /// Пока его нет, вход невозможен, и об этом надо сказать прямо в окне — иначе
    /// кнопка «войти» молча ничего не делала бы.
    /// </summary>
    public bool IsTwitchConfigured => TwitchVm.IsTwitchConfigured;

    /// <summary>Начать вход. Возвращает код для ввода на странице Twitch либо null, если вход невозможен или сервер отказал.</summary>
    public Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default) =>
        TwitchVm.BeginTwitchLoginAsync(cancellationToken);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    public Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default) =>
        TwitchVm.ContinueTwitchLoginAsync(challenge, cancellationToken);

    /// <summary>Выйти из аккаунта.</summary>
    public Task LogoutTwitchAsync(CancellationToken cancellationToken = default) =>
        TwitchVm.LogoutTwitchAsync(cancellationToken);

    /// <summary>
    /// Применяет настройки интеграции.
    ///
    /// Отдельно от прочих настроек, потому что применение здесь имеет побочный
    /// эффект: подписка на чат создаётся заново, а канал смениться может прямо
    /// сейчас, на эфире.
    /// </summary>
    public void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true) =>
        TwitchVm.ApplyTwitchSettings(updated, saveNow);

    /// <summary>Живые настройки интеграции (лежат в SettingsService, оттуда в settings.json).</summary>
    public TwitchSettings TwitchSettings => TwitchVm.TwitchSettings;

    /// <summary>
    /// Восстановить вход при старте приложения.
    ///
    /// Порядок тот же, что у дока и модуля озвучки: настройки уже прочитаны, поэтому
    /// канал известен и подписку можно создать сразу, не дожидаясь первого
    /// открытия окна.
    /// </summary>
    public Task RestoreTwitchAsync() => TwitchVm.RestoreTwitchAsync();

    /// <summary>Открыть адрес в браузере по умолчанию.</summary>
    public void OpenTwitchConsole() => TwitchVm.OpenTwitchConsole();

    /// <summary>Открыть адрес страницы активации кода входа.</summary>
    public void OpenTwitchActivation(string verificationUri) => TwitchVm.OpenTwitchActivation(verificationUri);
}