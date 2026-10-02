using SoundMeeter.Models;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Перенаправление звука приложений и список устройств/процессов.
    ///
    /// Значков здесь нет намеренно (SM-A10): метод <c>GetAppIcon(uint)</c> был
    /// единственным, кто отдавал <c>BitmapImage</c>, и не вызывался НИ РАЗУ —
    /// значки рисуют ViewModel'ы, а теперь их рисует UI по пути к файлу. Метод
    /// удалён вместе с кэшем иконок и <c>System.Drawing</c>: иначе WPF и
    /// System.Drawing.Common остались бы в ядре ради кода, который мёртв.
    /// </summary>
    public interface IAudioService
    {
        Task<List<AudioDevice>> GetAudioOutputDevicesAsync();
        Task<List<RunningApp>> GetRunningAppsWithAudioAsync();
        Task<(bool Success, string Error)> SetAppAudioDeviceAsync(uint processId, string deviceId);
        string LastError { get; }
        event EventHandler? AudioDevicesChanged;
        event EventHandler? AppsChanged;
    }
}
