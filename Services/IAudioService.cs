using SoundMeeter.Models;

namespace SoundMeeter.Services
{
    public interface IAudioService
    {
        Task<List<AudioDevice>> GetAudioOutputDevicesAsync();
        Task<List<RunningApp>> GetRunningAppsWithAudioAsync();
        Task<(bool Success, string Error)> SetAppAudioDeviceAsync(uint processId, string deviceId);
        System.Windows.Media.Imaging.BitmapImage? GetAppIcon(uint processId);
        string LastError { get; }
        event EventHandler? AudioDevicesChanged;
        event EventHandler? AppsChanged;
    }
}
