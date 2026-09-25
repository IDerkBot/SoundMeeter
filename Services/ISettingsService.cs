using SoundMeeter.Models;

namespace SoundMeeter.Services
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        void Save();
    }
}
