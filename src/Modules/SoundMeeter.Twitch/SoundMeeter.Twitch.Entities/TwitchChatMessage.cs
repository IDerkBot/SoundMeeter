namespace SoundMeeter.Twitch.Entities;

/// <summary>
/// Одно сообщение из EventSub WebSocket, разобранное до нужных полей.
///
/// Разбор отделён от транспорта намеренно: протокол меняется ( Twitch переводит
/// IRC в EventSub, и формат сообщений уже отличается), а вот что модулю нужно —
/// логин автора и текст реплики — останется прежним. Проверки разбора не должны
/// требовать сокета.
/// </summary>
public sealed record TwitchChatMessage(string User, string Text);
