using SoundMeeter.Services;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно выбора аудиоустройства для стрипа (входной источник или выходная шина).
    /// Первый элемент — None (снять назначение). Список фильтруется полем
    /// поиска и выпадающим списком типа устройства (MIC/SPK).
    /// </summary>
    public partial class DevicePickerWindow : Window
    {
        private enum DeviceKind
        {
            All,
            Microphone,
            Speaker,
        }

        private sealed record DeviceItem(string Icon, string Name, string DeviceId, DeviceKind Kind);

        private sealed record KindOption(string Label, DeviceKind Kind);

        private readonly List<DeviceItem> _items = new();
        private readonly DeviceItem _none;

        /// <summary>Текущий выбор. Не сбрасывается фильтрацией — если элемент скрыт,
        /// OK всё равно сохраняет прежнее назначение.</summary>
        private DeviceItem? _selected;

        /// <summary>Страховка от записи _selected при программной пересборке списка.</summary>
        private bool _refreshing;

        public string? SelectedDeviceId { get; private set; }

        public DevicePickerWindow(IReadOnlyList<DeviceInfo> catalog, bool forInput, bool allowLoopback = false)
        {
            InitializeComponent();
            Language = Loc.XmlLanguage;

            Title = Loc.Get(forInput ? "Sm.Picker.ChooseInput" : "Sm.Picker.ChooseOutput");

            _none = new DeviceItem("×", Loc.Get("Sm.Picker.None"), string.Empty, DeviceKind.All);
            _items.Add(_none);
            foreach (var device in catalog)
            {
                if (forInput)
                {
                    if (device.IsMicrophone)
                    {
                        _items.Add(new DeviceItem("●", Loc.Get("Sm.Picker.TagMic", device.Name),
                            device.DeviceId, DeviceKind.Microphone));
                    }
                    // Выход виртуального кабеля источником входа не бывает: в него
                    // играют приложения, а звук приходит в стрип входа того же кабеля.
                    else if (allowLoopback && !device.IsVirtualCable)
                    {
                        _items.Add(new DeviceItem("◄",
                            Loc.Get("Sm.Picker.TagSpk", device.Name, Loc.Get("Sm.Picker.Loopback")),
                            device.DeviceId, DeviceKind.Speaker));
                    }
                }
                else
                {
                    if (!device.IsMicrophone)
                        _items.Add(new DeviceItem("►", device.Name, device.DeviceId, DeviceKind.Speaker));
                }
            }

            _selected = _none;

            KindBox.ItemsSource = new[]
            {
                new KindOption(Loc.Get("Sm.Picker.KindAll"), DeviceKind.All),
                new KindOption(Loc.Get("Sm.Picker.KindMic"), DeviceKind.Microphone),
                new KindOption(Loc.Get("Sm.Picker.KindSpk"), DeviceKind.Speaker),
            };
            KindBox.SelectedIndex = 0;
            //  Для выхода все устройства — колонки, фильтр по типу не нужен.  */
            KindBox.Visibility = forInput ? Visibility.Visible : Visibility.Collapsed;

            ApplyFilter();
            Loaded += (_, _) => SearchBox.Focus();
        }

        /// <summary>
        /// Позиционирует выбор на текущем назначении стрипа.
        /// </summary>
        public void SelectCurrent(string? deviceId)
        {
            var current = _items.FirstOrDefault(i => i.DeviceId == (deviceId ?? string.Empty))
                        ?? _none;
            _selected = current;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string query = SearchBox.Text.Trim();
            var kind = (KindBox.SelectedItem as KindOption)?.Kind ?? DeviceKind.All;

            var visible = new List<DeviceItem>();

            //  (none) закреплён сверху, пока не включены фильтры.  */
            if (query.Length == 0 && kind == DeviceKind.All)
            {
                visible.Add(_none);
            }

            visible.AddRange(_items
                .Skip(1)
                .Where(i => kind == DeviceKind.All || i.Kind == kind)
                .Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));

            _refreshing = true;
            try
            {
                DeviceList.ItemsSource = visible;
                DeviceList.SelectedItem = visible.FirstOrDefault(i => ReferenceEquals(i, _selected));
            }
            finally
            {
                _refreshing = false;
            }

            EmptyHint.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OkButton.IsEnabled = visible.Count > 0;
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing) return;
            if (DeviceList.SelectedItem is DeviceItem item) _selected = item;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            SelectedDeviceId = _selected is null || string.IsNullOrEmpty(_selected.DeviceId)
                ? null
                : _selected.DeviceId;
            DialogResult = true;
        }
    }
}