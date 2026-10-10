using SoundMeeter.Core.Contract;
using SoundMeeter.TextToSpeech.Entities;

namespace SoundMeeter.TextToSpeech.Contract;

/// <summary>
/// То, что окну настроек синтеза речи нужно от приложения (SM-E01).
///
/// Отдельный контракт вместо прямой зависимости от главной VM по той же
/// причине, что у <c>IClipboardService</c> и <c>IUiTimer</c>: главная VM
/// поднимает двенадцать сервисов, запускает таймеры и фоновые потоки, а окну
/// настроек нужны четыре вещи. Через этот контракт окно проверяется без
/// поднятия всего приложения — с заглушкой движка и синтезатора.
/// </summary>
public interface ITextToSpeechHost
{
    /// <summary>Модуль синтеза речи.</summary>
    ITtsModule Tts { get; }

    /// <summary>Живые настройки модуля.</summary>
    TextToSpeechSettings TtsSettings { get; }

    /// <summary>Входные стрипы микшера — из них строится список целей.</summary>
    IReadOnlyList<IInputStrip> Inputs { get; }

    /// <summary>Применить настройки модуля, сохранив их (saveNow) или нет.</summary>
    void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true);
}
