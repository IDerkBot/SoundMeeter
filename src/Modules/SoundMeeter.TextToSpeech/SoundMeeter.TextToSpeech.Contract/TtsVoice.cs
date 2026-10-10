namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Голос синтеза, доступный в системе. Плоский снимок того, что SAPI знает о
/// голосе, — ядру нужны только эти поля, а типы SAPI (<c>VoiceInfo</c>) в
/// настройках и в разметке светить незачем.
/// </summary>
/// <param name="Name">Имя голоса, как его знает SAPI: его и пишут в настройки.</param>
/// <param name="Culture">Код языка и региона, например <c>ru-RU</c>.</param>
/// <param name="Gender">Пол голоса в терминах SAPI (Female/Male/NotSet).</param>
/// <param name="Description">Описание из системы, обычно с названием движка.</param>
public sealed record TtsVoice(string Name, string Culture, string Gender, string Description)
{
    /// <summary>
    /// Голос умеет русский. Сравнение по префиксу «ru», а не по точному равенству:
    /// <c>ru-RU</c> и <c>uk-UA</c> различаются, а вот <c>ru</c> без региона и
    /// <c>ru-RU</c> — один и тот же язык для наших целей.
    /// </summary>
    public bool IsRussian =>
        Culture.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    /// <summary>Подпись в списке: имя и язык, чтобы два «Microsoft Zira» не слились.</summary>
    public string Title => string.IsNullOrWhiteSpace(Culture) ? Name : $"{Name} ({Culture})";
}

/// <summary>
/// Каталог голосов системы. Отдельный контракт нужен, чтобы разбор команд и
/// окно настроек работали одинаково и с SAPI, и (в проверках) с заглушкой: имя
/// голоса приходит из настроек и из чата, то есть из данных, которые никто не
/// проверял на существование.
/// </summary>
public interface ITtsVoiceCatalog
{
    /// <summary>Доступен ли синтез в этой системе вообще.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему синтез недоступен (пусто — доступен).</summary>
    string UnavailableReason { get; }

    /// <summary>Все установленные голоса. Пусто, если синтез недоступен.</summary>
    IReadOnlyList<TtsVoice> Voices { get; }

    /// <summary>Найти голос по имени. null, если такого голоса в системе нет.</summary>
    TtsVoice? Find(string? name);

    /// <summary>
    /// Голос по умолчанию: заданный в настройках, иначе русский, иначе любой.
    ///
    /// Русский берётся раньше любого, потому что модуль задуман русскоязычным:
    /// без явного выбора говорить английским голосом по-русски — не то, что
    /// ожидает стример. Если русских голосов в системе нет, возвращается любой
    /// доступный, а вызывающий пишет об этом в журнал (см.
    /// <see cref="TextToSpeechService"/>).
    /// </summary>
    TtsVoice? Resolve(string? preferredName);
}
