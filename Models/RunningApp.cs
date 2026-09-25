namespace SoundMeeter.Models
{
    public class RunningApp
    {
        public uint ProcessId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string CurrentDeviceId { get; set; } = string.Empty;
        public bool HasAudioSession { get; set; }
        /// <summary>Windows хранит явный выход для приложения, а не системный по умолчанию.</summary>
        public bool HasExplicitRoute { get; set; }
        public string? ExecutablePath { get; set; }
    }
}
