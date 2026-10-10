using SoundMeeter.Core.Contract;
using SoundMeeter.TextToSpeech.Contract;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.TextToSpeech.Entities;

namespace SoundMeeter.Core.UseCases;

// Модуль синтеза речи (SM-E01) вынесен в TtsViewModel
// (SoundMeeter.TextToSpeech.UseCases). Этот partial оставлен для
// совместимости: все вызовы делегируются.
public partial class MainViewModel : ITextToSpeechHost
{
    /// <summary>Доступ к VM модуля синтеза речи (для окна настроек).</summary>
    public TtsViewModel TtsModule => _ttsVm;

    /// <summary>Модуль синтеза речи.</summary>
    public TextToSpeechService Tts => _ttsVm.Service;

    /// <summary>Модуль синтеза речи глазами окна настроек (узкий контракт).</summary>
    ITtsModule ITextToSpeechHost.Tts => _ttsVm.Tts;

    /// <summary>
    /// Полосы микшера для окна настроек. Реализация явная, потому что у главной VM
    /// <see cref="Inputs"/> — это <c>ObservableCollection</c>: класс, а интерфейс
    /// требует <c>IReadOnlyList</c>, и общего типа у них нет.
    /// </summary>
    IReadOnlyList<IInputStrip> ITextToSpeechHost.Inputs => _ttsVm.Inputs;

    /// <summary>Живые настройки модуля (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public TextToSpeechSettings TtsSettings => _ttsVm.TtsSettings;

    /// <summary>
    /// Применяет настройки модуля из окна. Побочный эффект (создание
    /// отдельного канала TTS) живёт в TtsViewModel.
    /// </summary>
    public void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true) =>
        _ttsVm.ApplyTtsSettings(updated, saveNow);

    /// <summary>
    /// Поднять модуль после восстановления пресета. Порядок тот же, что у дока:
    /// стрипы уже на месте, поэтому «куда говорить» разрешается сразу.
    /// </summary>
    public void RestoreTts() => _ttsVm.RestoreTts();
}
