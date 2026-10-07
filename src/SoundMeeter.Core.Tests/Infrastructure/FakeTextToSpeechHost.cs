using SoundMeeter.Models;
using SoundMeeter.Services.TextToSpeech;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Заглушка «приложения» для окна настроек синтеза речи (SM-E01).
///
/// Настоящий <see cref="MainViewModel"/> поднять в проверке нельзя: он тянет
/// двенадцать сервисов, запускает таймеры и фоновые потоки, а окну настроек нужны
/// четыре вещи. Через <see cref="ITextToSpeechHost"/> это урезается до движка,
/// синтезатора и списка полос — без побочных эффектов.
/// </summary>
public sealed class FakeTextToSpeechHost : ITextToSpeechHost
{
    private sealed class SilentSpeechEngine : ITtsSpeechEngine
    {
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public void Speak(string text, TtsSpeechOptions options, Action<float[]> onAudio,
            CancellationToken cancellationToken) => onAudio(new float[960]);

        public void Dispose() { }
    }

    private sealed class FixedVoiceCatalog : ITtsVoiceCatalog
    {
        public List<TtsVoice> Installed { get; } =
        [
            new("Microsoft Irina", "ru-RU", "Female", ""),
            new("Microsoft Zira", "en-US", "Female", ""),
        ];

        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public IReadOnlyList<TtsVoice> Voices => Installed;

        public TtsVoice? Find(string? name) => Installed.FirstOrDefault(
            voice => string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase));

        public TtsVoice? Resolve(string? preferred) =>
            Find(preferred) ?? Installed.FirstOrDefault(voice => voice.IsRussian) ?? Installed.FirstOrDefault();
    }

    private sealed class DirectDispatcher : Services.IDispatcherService
    {
        public bool HasThreadAccess => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    public FakeTextToSpeechHost(bool withStrips = true)
    {
        if (withStrips)
        {
            Engine.FakeInputs.Add(new InputChannelModel
            {
                Id = "strip-mic",
                Name = "MIC Микрофон",
                ChannelName = "Микрофон",
                IsMicrophone = true,
                DeviceId = "mic-device",
                IsAvailable = true,
            });
        }

        var settings = new AppSettings();
        TtsSettings = settings.TextToSpeech;

        Tts = new TextToSpeechService(Engine, new SilentSpeechEngine(), new FixedVoiceCatalog(),
            new ManualTtsMessageSource(), new NoopSettingsService(settings), new DirectDispatcher());

        foreach (var model in Engine.FakeInputs)
            Strips.Add(new InputChannelViewModel(model, Engine, [], Engine.Catalog, () => { }));
    }

    public FakeAudioEngine Engine { get; } = new();

    /// <summary>Полосы, созданные за время жизни заглушки.</summary>
    public List<InputChannelViewModel> Strips { get; } = new();

    IReadOnlyList<InputChannelViewModel> ITextToSpeechHost.Inputs => Strips;

    public TextToSpeechService Tts { get; }

    public TextToSpeechSettings TtsSettings { get; }

    /// <summary>Что ушло в модуль при последнем применении настроек.</summary>
    public TextToSpeechSettings? LastApplied { get; private set; }

    public void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true)
    {
        // Тот же маркер, что и у настоящей VM: пункт «отдельный канал» превращается
        // в реальный стрип ДО сохранения, иначе в settings.json уехал бы маркер.
        if (updated.TargetInputId == ViewModels.TtsTargetItemViewModel.GeneratedId)
        {
            updated.TargetInputId = Engine.EnsureGeneratedInput(
                ViewModels.MainViewModel.TextToSpeechGeneratedStripName);
            AddStrip(updated.TargetInputId);

            var created = Engine.Inputs.FirstOrDefault(input => input.Id == updated.TargetInputId);
            updated.TargetInputDeviceId = created?.DeviceId ?? "";
        }

        LastApplied = updated;
        Tts.Apply(updated, saveNow);
    }

    private void AddStrip(string stripId)
    {
        var model = Engine.Inputs.FirstOrDefault(input => input.Id == stripId);
        if (model is null || Strips.Any(strip => strip.Id == stripId)) return;

        Strips.Add(new InputChannelViewModel(model, Engine, [], Engine.Catalog, () => { }));
    }

    private sealed class NoopSettingsService(AppSettings settings) : Services.ISettingsService
    {
        public AppSettings Settings { get; } = settings;
        public void Save() { }
    }
}
