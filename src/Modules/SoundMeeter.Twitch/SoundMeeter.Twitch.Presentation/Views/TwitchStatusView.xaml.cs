using SoundMeeter.Twitch.Presentation.ViewModels;

namespace SoundMeeter.Twitch.Presentation.Views;

public partial class TwitchStatusView
{
    public TwitchStatusView()
    {
        InitializeComponent();
    }

    public TwitchStatusView(TwitchStatusViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
