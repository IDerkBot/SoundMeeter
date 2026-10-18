using SoundMeeter.Update.Entities;

namespace SoundMeeter.Update.Presentation.Events;

public class UpdateInstallRequestedEventArgs(UpdateInfo update) : EventArgs
{
    public UpdateInfo Update { get; } = update;
}