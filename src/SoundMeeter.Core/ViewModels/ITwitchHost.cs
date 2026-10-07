using SoundMeeter.Models;
using SoundMeeter.Services.Twitch;

namespace SoundMeeter.ViewModels;

/// <summary>
/// То, что окну настроек интеграции с Twitch нужно от приложения (SM-F01).
///
/// Отдельный контракт — по той же причине, что и <c>ITextToSpeechHost</c>,
/// <c>IClipboardService</c>, <c>IUiTimer</c>: главная VM поднимает много сервисов,
/// запускает таймеры и фоновые потоки, а окну настроек нужны четыре вещи. Через этот
/// контракт окно проверяется без поднятия всего приложения — с заглушками.
/// </summary>
public interface ITwitchHost
{
    /// <summary>Интеграция собрана: в коде есть client ID. Без него вход невозможен.</summary>
    bool IsTwitchConfigured { get; }

    /// <summary>Запросить код входа. null — вход невозможен или сервер отказал.</summary>
    Task<DeviceCodeChallenge?> BeginTwitchLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>Спросить, подтвердил ли пользователь вход.</summary>
    Task<DeviceAuthResult> ContinueTwitchLoginAsync(DeviceCodeChallenge challenge,
        CancellationToken cancellationToken = default);

    /// <summary>Выйти из аккаунта.</summary>
    Task LogoutTwitchAsync(CancellationToken cancellationToken = default);

    /// <summary>Применить настройки интеграции, сохранив их (saveNow) или нет.</summary>
    void ApplyTwitchSettings(TwitchSettings updated, bool saveNow = true);

    /// <summary>Открыть страницу регистрации приложения в Twitch.</summary>
    void OpenTwitchConsole();

    /// <summary>Открыть страницу активации кода входа.</summary>
    void OpenTwitchActivation(string verificationUri);
}