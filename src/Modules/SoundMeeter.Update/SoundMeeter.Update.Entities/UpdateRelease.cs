namespace SoundMeeter.Update.Entities;

/// <summary>
/// Запись в истории релизов: описание одной версии, которую пользователь
/// пропускает при обновлении. Отдельный тип от <see cref="UpdateInfo"/>, потому
/// что у промежуточного релиза нет ни ассета, ни роли цели обновления — это
/// просто заголовок версии и её заметки.
/// </summary>
public sealed class UpdateRelease
{
    public required AppVersion Version { get; init; }
    public string TagName { get; init; } = "";
    public string Title { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
}
