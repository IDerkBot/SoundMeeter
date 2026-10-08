namespace SoundMeeter.Models
{
    public class InstalledApp
    {
        public string Name { get; set; } = string.Empty;
        public string? InstallLocation { get; set; }
        public string? Publisher { get; set; }
        public string? Version { get; set; }
        public string? IconPath { get; set; }
        public string? ExecutablePath { get; set; }
    }
}
