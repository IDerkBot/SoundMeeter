namespace SoundMeeter.Update.Entities;

/// <summary>Найденный релиз, который новее установленной версии.</summary>
public sealed class UpdateInfo
{
    public required AppVersion Version { get; init; }
    public string TagName { get; init; } = "";
    public string Title { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Portable-архив для автоустановки. null — у релиза нет подходящего zip.</summary>
    public UpdateAsset? Asset { get; init; }

    /// <summary>
    /// Что пользователь пропускает: все релизы новее установленной версии до цели
    /// обновления включительно, от новых к старым. Первый элемент — целевой релиз,
    /// то есть тот же, что и в <see cref="Version"/> и <see cref="ReleaseNotes"/>.
    ///
    /// Без этого обновление через несколько версий показывало только записи
    /// последней: «поставил 0.0.7», а что было в 0.0.2…0.0.6 — потеряно.
    /// </summary>
    public IReadOnlyList<UpdateRelease> Changelog { get; init; } = [];

    /// <summary>Сколько промежуточных версий между установленной и целевой.</summary>
    public int SkippedReleases => Math.Max(0, Changelog.Count - 1);

    /// <summary>Автоустановка возможна только при наличии zip-ассета.</summary>
    public bool CanInstall => Asset is not null;
}
