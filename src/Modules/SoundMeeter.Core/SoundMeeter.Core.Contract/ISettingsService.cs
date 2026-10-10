using SoundMeeter.Core.Entities;

namespace SoundMeeter.Core.Contract
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        void Save();
    }
}
