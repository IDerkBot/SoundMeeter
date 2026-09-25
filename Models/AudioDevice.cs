namespace SoundMeeter.Models
{
    public class AudioDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
    }
}
