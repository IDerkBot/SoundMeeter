using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Speech.Synthesis;

namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Каталог голосов SAPI: что установлено в системе Windows.
///
/// Список голосов зависит от установленных языковых пакетов, поэтому он
/// читается один раз и кэшируется: перечисление уходит в SAPI и занимает
/// заметное время, а список за время работы приложения не меняется (только
/// после установки нового голоса, что требует перезапуска).
/// </summary>
public sealed class SapiVoiceCatalog : ITtsVoiceCatalog
{
    private readonly ILogger _logger = AppLog.For<SapiVoiceCatalog>();
    private readonly Lazy<(IReadOnlyList<TtsVoice> Voices, string Reason)> _loaded;

    public SapiVoiceCatalog() =>
        _loaded = new Lazy<(IReadOnlyList<TtsVoice>, string)>(
            LoadVoices, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => _loaded.Value.Reason.Length == 0;

    public string UnavailableReason => _loaded.Value.Reason;

    public IReadOnlyList<TtsVoice> Voices => _loaded.Value.Voices;

    public TtsVoice? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var wanted = name.Trim();
        return Voices.FirstOrDefault(voice =>
            string.Equals(voice.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public TtsVoice? Resolve(string? preferredName)
    {
        if (Find(preferredName) is { } preferred) return preferred;

        var russian = Voices.FirstOrDefault(voice => voice.IsRussian);
        if (russian is not null) return russian;

        return Voices.FirstOrDefault();
    }

    private (IReadOnlyList<TtsVoice>, string) LoadVoices()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            var voices = synthesizer.GetInstalledVoices()
                .Where(installed => installed.Enabled)
                .Select(installed => installed.VoiceInfo)
                .Where(info => !string.IsNullOrWhiteSpace(info.Name))
                .Select(info => new TtsVoice(info.Name, info.Culture.Name, info.Gender.ToString(), info.Description ?? ""))
                .OrderBy(voice => voice.IsRussian ? 0 : 1)
                .ThenBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (voices.Count == 0)
            {
                // Не ошибка, а очень частый случай на свежей Windows: голоса есть
                // всегда, но ни одного включённого — значит, сломан сам реестр SAPI.
                _logger.LogWarning("SAPI не вернул ни одного включённого голоса");
                return (Array.Empty<TtsVoice>(), Loc.Get("Sm.Tts.NoVoices"));
            }

            int russian = voices.Count(voice => voice.IsRussian);
            _logger.LogInformation("SAPI: {Total} голосов, из них русских {Russian}: {Names}",
                voices.Count, russian, string.Join(", ", voices.Select(voice => voice.Title)));

            return (voices, "");
        }
        catch (Exception ex)
        {
            // Например, в контейнере без SAPI или без аудиоустройств: модуль
            // обязан остаться выключенным и объяснить почему, а не падать.
            _logger.LogError(ex, "SAPI недоступен: {Message}", ex.Message);
            return (Array.Empty<TtsVoice>(), Loc.Get("Sm.Tts.EngineUnavailable", ex.Message));
        }
    }
}
