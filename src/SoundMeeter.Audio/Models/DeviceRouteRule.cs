namespace SoundMeeter.Models
{
    public class DeviceRouteRule
    {
        public string ExecutablePath { get; set; } = string.Empty;
        /// <summary>Пустой endpoint — отложенный сброс выхода при появлении аудиосессии.</summary>
        public string DeviceId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string? IconPath { get; set; }
    }
}
