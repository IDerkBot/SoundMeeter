using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Docking.Presentation.ViewModels
{
    /// <summary>Канал микшера в списке выбора для дока.</summary>
    public sealed partial class ObsDockChannelItemViewModel : LocalizedViewModel
    {
        public ObsDockChannelItemViewModel(string id, string kind, string title, string deviceName, string deviceId,
            bool isAvailable)
        {
            Id = id;
            Kind = kind;
            Title = title;
            DeviceName = deviceName;
            DeviceId = deviceId;
            IsAvailable = isAvailable;
        }

        public string Id { get; }

        /// <summary>ObsDockChannels.Input или Output.</summary>
        public string Kind { get; }

        public string Title { get; }

        public string DeviceName { get; }

        /// <summary>Устройство стрипа: выбор переживает пересоздание стрипа движком.</summary>
        public string DeviceId { get; }

        public bool IsAvailable { get; }

        /// <summary>Показывать этот канал в доке (в дополнение к «включить все»).</summary>
        [ObservableProperty]
        private bool _isSelected;

        /// <summary>Что показать в списке: имя канала, а если оно совпадает с устройством — только его.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? DeviceName : Title;

        public string Tooltip => string.IsNullOrWhiteSpace(DeviceName) || DeviceName == Title
            ? DisplayName
            : $"{Title}  ({DeviceName})";
    }
}
