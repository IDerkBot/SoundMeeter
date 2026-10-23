using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Entities;

namespace SoundMeeter.Twitch.Presentation.ViewModels;

/// <summary>
/// Компактный ViewModel для отображения статуса Twitch в title bar.
/// Показывает имя пользователя и индикатор трансляции (Live/Offline).
/// </summary>
public sealed partial class TwitchStatusViewModel : ObservableObject, IDisposable
{
    private readonly ITwitchState _twitch;
    private bool _disposed;

    public TwitchStatusViewModel(ITwitchState twitch)
    {
        _twitch = twitch;
        twitch.StatusChanged += OnStatusChanged;
        Refresh();
    }

    [ObservableProperty]
    private string _userName = "";

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private string _statusText = "";

    private void OnStatusChanged() => Refresh();

    private void Refresh()
    {
        UserName = _twitch.IsAuthorized ? _twitch.AuthorizedLogin : "";
        IsLive = _twitch.StreamState == TwitchStreamState.Live;
        StatusText = _twitch.StreamState switch
        {
            TwitchStreamState.Live => "Live",
            _ => "Offline"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _twitch.StatusChanged -= OnStatusChanged;
    }
}
