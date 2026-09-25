using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;

namespace SoundMeeter.ViewModels
{
    public partial class AudioDeviceViewModel : ObservableObject
    {
        private readonly IAudioService _audioService;
        private readonly Action<AudioDeviceViewModel> _onHideRequested;
        private readonly Action<ConfiguredAppViewModel> _onRemoveRuleRequested;

        [ObservableProperty]
        private AudioDevice _device = new();

        [ObservableProperty]
        private bool _isDropTarget;

        public ObservableCollection<AppViewModel> AssignedApps { get; } = new();
        public ObservableCollection<ConfiguredAppViewModel> ConfiguredApps { get; } = new();

        public AudioDeviceViewModel(
            IAudioService audioService,
            Action<AudioDeviceViewModel> onHideRequested,
            Action<ConfiguredAppViewModel> onRemoveRuleRequested)
        {
            _audioService = audioService;
            _onHideRequested = onHideRequested;
            _onRemoveRuleRequested = onRemoveRuleRequested;
        }

        public string Name => Device.Name;
        public string Id => Device.Id;
        public bool IsDefault => Device.IsDefault;

        [RelayCommand]
        private async Task RemoveAppAsync(AppViewModel? app)
        {
            if (app == null) return;
            await _audioService.SetAppAudioDeviceAsync(app.ProcessId, string.Empty);
        }

        [RelayCommand]
        private void RemoveConfiguredApp(ConfiguredAppViewModel app)
        {
            _onRemoveRuleRequested?.Invoke(app);
        }

        [RelayCommand]
        private void HideDevice()
        {
            _onHideRequested?.Invoke(this);
        }
    }
}
