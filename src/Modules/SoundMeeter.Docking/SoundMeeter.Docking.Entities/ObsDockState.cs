namespace SoundMeeter.Docking.Entities
{
    /// <summary>Снимок состояния микшера для дока: каналы + признак «движок запущен».</summary>
    public sealed class ObsDockState
    {
        public bool EngineRunning { get; init; }
        public IReadOnlyList<ObsDockChannelState> Channels { get; init; } = Array.Empty<ObsDockChannelState>();

        /// <summary>Нижняя граница фейдера в дБ (совпадает с VoiceFader в MixerTheme).</summary>
        public float MinDb { get; init; } = -60f;

        /// <summary>Верхняя граница фейдера в дБ.</summary>
        public float MaxDb { get; init; } = 12f;

        public ObsDockState() { }

        public ObsDockState(bool engineRunning, IReadOnlyList<ObsDockChannelState> channels)
        {
            EngineRunning = engineRunning;
            Channels = channels;
        }
    }
}
