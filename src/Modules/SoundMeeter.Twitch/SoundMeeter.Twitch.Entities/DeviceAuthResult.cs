namespace SoundMeeter.Twitch.Entities;

/// <summary>Ответ на попытку забрать токен: состояние и, при успехе, сама пара.</summary>
public sealed record DeviceAuthResult(DeviceAuthStatus Status, TwitchToken? Token);
