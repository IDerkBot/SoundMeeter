namespace SoundMeeter.Twitch.Contract;

/// <summary>
/// Разговор с Helix API Twitch (SM-F01).
///
/// Интерфейс — по той же причине, что и <see cref="ITwitchAuth"/>: проверки
/// интеграции не должны ходить в сеть за статусом трансляции.
/// </summary>
public interface ITwitchApi
{
    /// <summary>
    /// Идёт ли трансляция. null — неизвестно: сеть недоступна или канала нет.
    ///
    /// Различать «не в эфире» и «неизвестно» обязательно, и это не формальность:
    /// обрыв сети не должен выглядеть на эфире как завершившаяся трансляция.
    /// </summary>
    Task<bool?> IsLiveAsync(string channelLogin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Идентификатор канала по логину. null — канала нет, логина нет или сети нет.
    ///
    /// Нужен потому, что подписка на чат адресуется идентификатором, а не логином.
    /// </summary>
    Task<string?> ResolveChannelUserIdAsync(string channelLogin, CancellationToken cancellationToken = default);
}
