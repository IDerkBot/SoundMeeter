using SoundMeeter.Core.Contract;
using SoundMeeter.TextToSpeech.Contract;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.TextToSpeech.Entities;


namespace SoundMeeter.Core.Presentation.ViewModels;

// Модуль синтеза речи (SM-E01) вынесен в TtsViewModel
// (SoundMeeter.TextToSpeech.UseCases). Этот partial оставлен для
// совместимости: все вызовы делегируются.
//
// Главная VM берёт тот же экземпляр TtsViewModel, что и раньше: его настройки
// лежат в том же settings.json, а команда !ttsvoice пишет их мимо кнопки
// «Применить», поэтому оба должны видеть один модуль. Владеет им теперь
// MixerCoordinator — модуль живёт столько же, сколько приложение.
public partial class MainViewModel
{
    private TtsViewModel TtsVm => _coordinator.Tts;

    /// <summary>Доступ к VM модуля синтеза речи (для окна настроек).</summary>
    public TtsViewModel TtsModule => TtsVm;

    /// <summary>Модуль синтеза речи.</summary>
    public TextToSpeechService Tts => TtsVm.Service;

    /// <summary>Модуль синтеза речи глазами окна настроек (узкий контракт).</summary>
    ITtsModule ITextToSpeechHost.Tts => TtsVm.Tts;

    /// <summary>
    /// Полосы микшера для окна настроек. Список отдаёт модуль, а не эта VM: он
    /// видит стрипы через IStripProvider, и отдельная реализация интерфейса
    /// здесь была бы вторым источником правды о том, какие полосы есть.
    /// </summary>
    IReadOnlyList<IInputStrip> ITextToSpeechHost.Inputs => TtsVm.Inputs;

    /// <summary>Живые настройки модуля (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public TextToSpeechSettings TtsSettings => TtsVm.TtsSettings;

    /// <summary>
    /// Применяет настройки модуля из окна. Побочный эффект (создание
    /// отдельного канала TTS) живёт в TtsViewModel.
    /// </summary>
    public void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true) =>
        TtsVm.ApplyTtsSettings(updated, saveNow);

    /// <summary>
    /// Поднять модуль после восстановления пресета. Порядок тот же, что у дока:
    /// стрипы уже на месте, поэтому «куда говорить» разрешается сразу.
    /// </summary>
    public void RestoreTts() => _coordinator.RestoreTts();
}