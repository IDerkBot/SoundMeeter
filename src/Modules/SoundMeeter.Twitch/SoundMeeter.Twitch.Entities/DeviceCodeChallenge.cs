namespace SoundMeeter.Twitch.Entities;

/// <summary>
/// Код входа, который надо показать пользователю, и секретная его часть.
///
/// <see cref="DeviceCode"/> в интерфейс не показывается: его не вводят руками, его
/// приложение само предъявляет серверу при обмене. Пользователь видит только
/// <see cref="UserCode"/> — короткий и переносимый с экрана на клавиатуру.
/// </summary>
public sealed record DeviceCodeChallenge(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int IntervalSeconds)
{
    /// <summary>Время жизни кода. По нему вызывающий прекращает опрос.</summary>
    public TimeSpan Lifetime => TimeSpan.FromSeconds(ExpiresIn);

    /// <summary>Как часто сервер разрешает спрашивать.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>
    /// Когда код перестанет работать. Нужен, чтобы опрос не шёл вечно: без срока
    /// пользователь, закрывший страницу и ушедший, оставил бы приложение
    /// опрашивать сервер до бесконечности.
    /// </summary>
    public DateTimeOffset Deadline { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Код ещё можно предъявлять серверу.</summary>
    public bool IsAlive => DateTimeOffset.UtcNow < Deadline;
}
