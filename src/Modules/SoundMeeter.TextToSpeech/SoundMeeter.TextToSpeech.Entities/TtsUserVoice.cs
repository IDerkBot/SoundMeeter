namespace SoundMeeter.TextToSpeech.Entities;

/// <summary>
/// Голос, выбранный пользователем чата. Отдельный класс, а не пара в словаре,
/// потому что в settings.json нужен читаемый список, а не запись
/// <c>"login": {"voice": ...}</c>.
/// </summary>
public class TtsUserVoice
{
    /// <summary>Логин без «@». Сравнивается без учёта регистра.</summary>
    public string User { get; set; } = "";

    /// <summary>Имя голоса SAPI.</summary>
    public string Voice { get; set; } = "";

    public TtsUserVoice() { }

    public TtsUserVoice(string user, string voice)
    {
        User = user;
        Voice = voice;
    }

    public TtsUserVoice Clone() => new(User, Voice);
}
