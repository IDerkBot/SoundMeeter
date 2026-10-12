using SoundMeeter.TextToSpeech.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TextToSpeech.UseCases;

/// <summary>
/// Разбор сообщений чата в команды модуля (SM-E01).
///
/// Вынесен отдельно от синтеза и от сервиса намеренно: это чистая функция от
/// строки, без SAPI, без очереди и без потоков. Подключение к чату Twitch — это
/// только источник сообщений, и когда оно появится, весь этот класс останется
/// неизменным — а значит, его можно и нужно проверять обычными тестами.
///
/// Правила разбора, и почему именно такие:
/// <list type="bullet">
/// <item>команда должна быть в начале реплики (после пробелов) — иначе
///       «а ещё <c>!tts</c> привет» прочиталось бы вслух целиком;</item>
/// <item>регистр команды не важен («<c>!TTS</c>» пишут постоянно), а регистр
///       текста и логина — важен, это данные пользователя;</item>
/// <item>лишние пробелы схлопываются: синтезатор иначе читает паузы, которых
///       никто не писал;</item>
/// <item>ссылки отбрасываются: TTS-боты и плагины добавляют их в каждое
///       сообщение, а вслух они не произносятся;</item>
/// <item>длинная реплика обрезается, а не отбрасывается: обрезанное сообщение
///       пользователь хотя бы услышит.</item>
/// </list>
/// </summary>
public static class TtsChatCommandParser
{
    /// <summary>Схема ссылки: http(s), ftp или просто www.</summary>
    private static readonly string[] LinkPrefixes =
        ["http://", "https://", "ftp://", "www."];

    /// <summary>
    /// Разбирает реплику чата.
    /// </summary>
    /// <param name="user">Логин автора. Нормализуется: «@» убирается.</param>
    /// <param name="text">Текст реплики как есть.</param>
    /// <param name="settings">Настройки модуля: команды, пределы, списки.</param>
    /// <param name="isKnownVoice">
    /// Есть ли голос с таким именем в системе. Внедряется вызывающим, чтобы
    /// разбор не зависел от SAPI и оставался проверяемым: «<c>!ttsvoice</c> с
    /// несуществующим голосом» должно отклоняться, а не молча оставлять
    /// пользователя с прошлым голосом.
    /// </param>
    public static TtsChatCommand Parse(string? user, string? text, TextToSpeechSettings settings,
        Func<string, bool> isKnownVoice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(isKnownVoice);

        string login = NormalizeUser(user);
        string message = (text ?? "").Trim();

        if (message.Length == 0)
            return TtsChatCommand.Rejected(login, TtsChatReject.NotACommand);

        // Список игнора проверяем до разбора команд: игнорируемому пользователю
        // не положено ни озвучивания, ни смены голоса.
        if (settings.IgnoredUsers.Any(entry =>
                string.Equals(entry, login, StringComparison.OrdinalIgnoreCase)))
            return TtsChatCommand.Rejected(login, TtsChatReject.IgnoredUser);

        var voiceCommand = NormalizeCommand(settings.VoiceCommand, TextToSpeechSettings.DefaultVoiceCommand);
        var speakCommand = NormalizeCommand(settings.SpeakCommand, TextToSpeechSettings.DefaultSpeakCommand);

        // Команда голоса проверяется первой: «!ttsvoice» начинается с «!tts», и
        // при обратном порядке смена голоса читалась бы как фраза «voice ...».
        if (Matches(message, voiceCommand, out string? voiceArgument))
        {
            if (!settings.AllowVoiceChange)
                return TtsChatCommand.Rejected(login, TtsChatReject.VoiceChangeDisabled);

            string requested = Clean(voiceArgument!);
            if (requested.Length == 0)
                return TtsChatCommand.Rejected(login, TtsChatReject.EmptyText);

            if (requested.Length > TextToSpeechSettings.Limits.VoiceNameMaxLength)
                requested = requested[..TextToSpeechSettings.Limits.VoiceNameMaxLength];

            // Неизвестный голос отвергаем: принять его означало бы записать в
            // настройки имя, которого нет, и пользователь обнаружил бы это через
            // эфир, а не в окне настроек.
            if (!isKnownVoice(requested))
                return TtsChatCommand.Rejected(login, TtsChatReject.UnknownVoice);

            return TtsChatCommand.SetVoice(login, requested);
        }

        if (Matches(message, speakCommand, out string? speakArgument))
        {
            string spoken = Clean(speakArgument!);
            if (spoken.Length == 0)
                return TtsChatCommand.Rejected(login, TtsChatReject.EmptyText);

            int limit = Math.Max(1, settings.MaxMessageChars);
            bool truncated = spoken.Length > limit;
            if (truncated) spoken = spoken[..limit].TrimEnd();

            return TtsChatCommand.Speak(login, spoken, truncated);
        }

        return TtsChatCommand.Rejected(login, TtsChatReject.NotACommand);
    }

    /// <summary>Логин без «@», обрезанный по разумной длине.</summary>
    public static string NormalizeUser(string? user)
    {
        string login = (user ?? "").Trim().TrimStart('@').Trim();
        return login.Length > TextToSpeechSettings.Limits.UserNameMaxLength
            ? login[..TextToSpeechSettings.Limits.UserNameMaxLength]
            : login;
    }

    /// <summary>Приводит команду к виду «!что-то» в нижнем регистре.</summary>
    private static string NormalizeCommand(string? command, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(command) ? fallback : command.Trim();
        if (!value.StartsWith('!')) value = "!" + value;

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Проверяет, что реплика начинается с команды, и достаёт остаток.
    /// Регистр команды не важен, поэтому сравнение идёт по приведённой строке.
    /// </summary>
    private static bool Matches(string message, string command, out string? argument)
    {
        argument = null;
        if (message.Length < command.Length) return false;
        if (!message.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return false;

        // «!ttsfoo» — это не «!tts», и «!ttsfoo» не должно читаться вслух.
        if (message.Length > command.Length && !char.IsWhiteSpace(message[command.Length]))
            return false;

        argument = message[command.Length..];
        return true;
    }

    /// <summary>
    /// Готовит текст к произнесению: убирает ссылки, схлопывает пробелы и
    /// обрезает края.
    /// </summary>
    private static string Clean(string text)
    {
        var words = new List<string>(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (string word in words)
        {
            if (IsLink(word)) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(word);
        }

        return builder.ToString().Trim();
    }

    private static bool IsLink(string word)
    {
        // Знак препинания в конце («...») не делает слово не-ссылкой: «см.
        // twitch.tv» всё равно никто не хочет слушать вслух.
        string trimmed = word.TrimEnd('.', ',', '!', '?', ';', ':', ')');
        if (trimmed.Length == 0) return false;

        foreach (string prefix in LinkPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
