using SoundMeeter.TextToSpeech.Entities;

namespace SoundMeeter.TextToSpeech.Presentation;

/// <summary>Входной стрип в списке выбора цели для голоса.</summary>
public sealed class TtsTargetItemViewModel
{
    /// <summary>
    /// Пункт «отдельный канал TTS». Его Id — маркер, а не настоящий Id стрипа;
    /// настоящий появляется после создания (см. TtsViewModel.ApplyTtsSettings).
    /// </summary>
    public const string GeneratedId = TtsStripNames.GeneratedSentinel;

    public TtsTargetItemViewModel(string id, string title, string deviceId, bool isAvailable, bool isGenerated)
    {
        Id = id;
        Title = title;
        DeviceId = deviceId;
        IsAvailable = isAvailable;
        IsGenerated = isGenerated;
    }

    /// <summary>Id стрипа либо <see cref="GeneratedId"/>.</summary>
    public string Id { get; }

    /// <summary>Имя канала: своё, если задано, иначе имя устройства.</summary>
    public string Title { get; }

    /// <summary>DeviceId устройства стрипа (пусто у генерируемого).</summary>
    public string DeviceId { get; }

    /// <summary>Устройство на месте.</summary>
    public bool IsAvailable { get; }

    /// <summary>Канал без устройства, наполняемый модулем синтеза.</summary>
    public bool IsGenerated { get; }
}