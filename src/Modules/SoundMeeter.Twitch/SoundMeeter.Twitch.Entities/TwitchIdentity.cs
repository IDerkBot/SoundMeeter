namespace SoundMeeter.Twitch.Entities;

/// <summary>Логин и идентификатор аккаунта по токену.</summary>
public sealed record TwitchIdentity(string Login, string UserId);
