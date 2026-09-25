using SoundMeeter.Services;
using System.Collections.Generic;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно выбора аудиоустройства для стрипа (входной источник или выходная шина).
    /// Первый элемент — None (снять назначение).
    /// </summary>
    public partial class DevicePickerWindow : Window
    {
        private sealed record DeviceItem(string Icon, string Name, string DeviceId);

        private readonly List<DeviceItem> _items = new();
        private readonly bool _allowLoopback;

        public string? SelectedDeviceId { get; private set; }

        public DevicePickerWindow(IReadOnlyList<DeviceInfo> catalog, bool forInput, bool allowLoopback = false)
        {
            InitializeComponent();

            _allowLoopback = allowLoopback;
            Title = forInput ? "Choose input source" : "Choose output device";

            _items.Add(new DeviceItem("×", "(none)", ""));
            foreach (var device in catalog)
            {
                if (forInput)
                {
                    if (device.IsMicrophone)
                    {
                        _items.Add(new DeviceItem("●", $"MIC  {device.Name}", device.DeviceId));
                    }
                    else if (allowLoopback)
                    {
                        _items.Add(new DeviceItem("◄", $"SPK  {device.Name}  (loopback)", device.DeviceId));
                    }
                }
                else
                {
                    if (!device.IsMicrophone)
                        _items.Add(new DeviceItem("►", device.Name, device.DeviceId));
                }
            }

            DeviceList.ItemsSource = _items;
            DeviceList.SelectedIndex = 0;
        }

        /// <summary>
        /// Позиционирует выбор на текущем назначении стрипа.
        /// </summary>
        public void SelectCurrent(string? deviceId)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].DeviceId == deviceId)
                {
                    DeviceList.SelectedIndex = i;
                    return;
                }
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (DeviceList.SelectedItem is DeviceItem item)
            {
                SelectedDeviceId = string.IsNullOrEmpty(item.DeviceId) ? null : item.DeviceId;
                DialogResult = true;
            }
            else
            {
                DialogResult = true;
                SelectedDeviceId = null;
            }
        }
    }
}