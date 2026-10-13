using SoundMeeter.Infrastructure.Entities;

namespace SoundMeeter.Infrastructure.Contract
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        void Save();

        AppSettings CreateFullSnapshot();
    }
}
