using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.ChangeLanguage.UseCases;

namespace SoundMeeter.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // Поведение приложения: трей и автозапуск
    // =========================================================================
    [ObservableProperty]
    private bool _trayEnabled = true;

    [ObservableProperty]
    private bool _runAtStartup;

    public void RestoreAppBehaviour()
    {
        _behaviour.Restore();
        SyncBehaviourFlags();
    }

    private void SyncBehaviourFlags()
    {
        TrayEnabled = _behaviour.TrayEnabled;
        RunAtStartup = _behaviour.RunAtStartupInSystem;
    }

    partial void OnTrayEnabledChanged(bool value)
    {
        _settingsService.Settings.TrayEnabled = value;
        OnApplyTraySettings?.Invoke();
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        bool applied = _behaviour.ApplyRunAtStartup(value);
        if (applied == value) return;

        NotifyTray?.Invoke(
            Loc.Get("Sm.Tray.StartupFailedTitle"),
            Loc.Get("Sm.Tray.StartupFailedMessage", value ? Loc.Get("Sm.Tray.On") : Loc.Get("Sm.Tray.Off")));

        RunAtStartup = applied;
    }
}
