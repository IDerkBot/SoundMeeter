using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Docking.Entities;
using SoundMeeter.Docking.UseCases;
using System.Collections.ObjectModel;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Docking.Presentation.ViewModels
{
    /// <summary>
    /// Окно настроек док-панели OBS: включать ли сервер, на каком порту, какие
    /// каналы в него отдавать и как добавить сам док в OBS (правкой его user.ini).
    /// Изменения применяются кнопкой Apply и сразу сохраняются в settings.json.
    /// </summary>
    public sealed partial class ObsDockSettingsViewModel : LocalizedViewModel
    {
        private readonly DockController _dock;
        private readonly IClipboardService _clipboard;

        public ObsDockSettingsViewModel(DockController dock, IClipboardService clipboard)
        {
            _dock = dock;
            _clipboard = clipboard;

            var settings = dock.DockSettings;
            Enabled = settings.Enabled;
            ShowAllInputs = settings.ShowAllInputs;
            ShowAllOutputs = settings.ShowAllOutputs;
            PortText = settings.EffectivePort.ToString();

            foreach (var vm in dock.Inputs)
                InputChannels.Add(new ObsDockChannelItemViewModel(vm.Id, ObsDockChannels.Input, vm.Title,
                    vm.Name, vm.DeviceId, true));

            foreach (var vm in dock.Buses)
                OutputChannels.Add(new ObsDockChannelItemViewModel(vm.Id, ObsDockChannels.Output, vm.Title,
                    vm.Name, vm.DeviceId, vm.IsAvailable && !string.IsNullOrWhiteSpace(vm.DeviceId)));

            MarkSelected(settings);

            _dock.DockStatusChanged += RefreshStatus;
            RefreshStatus();
        }

        /// <summary>Поднимать сервер дока (в том числе автостарт при запуске приложения).</summary>
        [ObservableProperty]
        private bool _enabled;

        /// <summary>Порт сервера (текстом — чтобы не мешать вводе).</summary>
        [ObservableProperty]
        private string _portText;

        /// <summary>Отдавать в док все входные стрипы.</summary>
        [ObservableProperty]
        private bool _showAllInputs;

        /// <summary>Отдавать в док все выходные шины.</summary>
        [ObservableProperty]
        private bool _showAllOutputs;

        /// <summary>Инпуты, отмеченные пользователем.</summary>
        public ObservableCollection<ObsDockChannelItemViewModel> InputChannels { get; } = new();

        /// <summary>Выходы, отмеченные пользователем.</summary>
        public ObservableCollection<ObsDockChannelItemViewModel> OutputChannels { get; } = new();

        /// <summary>Индикатор подключённых панелей.</summary>
        [ObservableProperty]
        private string _status = "";

        /// <summary>Результат установки/удаления дока в OBS (зелёный/красный — по IsStatusOk).</summary>
        [ObservableProperty]
        private string _obsStatus = "";

        [ObservableProperty]
        private bool _isStatusOk;

        public string DockUrl => _dock.DockUrl;

        public bool IsServerRunning => _dock.IsDockRunning;

        /// <summary>Текст кнопки: сервер можно и поднять, и погасить прямо из окна.</summary>
        public string ServerToggleText => _dock.IsDockRunning
            ? Loc.Get("Sm.Obs.StopServer")
            : Loc.Get("Sm.Obs.StartServer");

        /// <summary>Док уже прописан в конфигурации OBS.</summary>
        public string ObsDockStateText => _dock.IsObsRunning
            ? Loc.Get("Sm.Obs.State.ObsRunning")
            : _dock.IsDockInstalledInObs
                ? Loc.Get("Sm.Obs.State.Registered")
                : Loc.Get("Sm.Obs.State.NotRegistered");

        [RelayCommand]
        private void Apply()
        {
            if (!TryReadPort(out int port))
            {
                SetObsStatus(Loc.Get("Sm.Obs.Status.InvalidPort", PortText), false);
                return;
            }

            var updated = BuildSettings(port);
            _dock.ApplyDockSettings(updated);
            RefreshStatus();

            SetObsStatus(_dock.DockError.Length > 0
                ? _dock.DockError
                : Loc.Get("Sm.Obs.Status.Saved", _dock.GetDockChannelPreview().Count, DockUrl),
                _dock.DockError.Length == 0);
        }

        [RelayCommand]
        private void ToggleServer()
        {
            if (_dock.IsDockRunning)
            {
                _dock.StopDock();
            }
            else if (!TryReadPort(out int port))
            {
                SetObsStatus(Loc.Get("Sm.Obs.Status.InvalidPort", PortText), false);
                return;
            }
            else
            {
                // Порт применяем сразу, иначе сервер поднимется на старом.
                _dock.ApplyDockSettings(BuildSettings(port));
            }

            OnPropertyChanged(nameof(ServerToggleText));
            OnPropertyChanged(nameof(IsServerRunning));
            RefreshStatus();

            if (_dock.DockError.Length > 0) SetObsStatus(_dock.DockError, false);
        }

        [RelayCommand]
        private void CopyUrl()
        {
            try
            {
                _clipboard.SetText(DockUrl);
                SetObsStatus(Loc.Get("Sm.Obs.Status.UrlCopied"), true);
            }
            catch (Exception ex)
            {
                SetObsStatus(Loc.Get("Sm.Obs.Status.CopyFailed", ex.Message), false);
            }
        }

        [RelayCommand]
        private void InstallInObs()
        {
            if (!TryReadPort(out int port))
            {
                SetObsStatus(Loc.Get("Sm.Obs.Status.InvalidPort", PortText), false);
                return;
            }

            // Адрес в OBS обязан совпадать с тем, на котором реально работает сервер.
            if (!_dock.IsDockRunning || _dock.DockServer.Port != port)
            {
                if (!_dock.StartDockFor(port, out var startError))
                {
                    SetObsStatus(startError, false);
                    return;
                }
            }

            var result = _dock.InstallDockInObs();
            SetObsStatus(result.Message, result.Success);
            OnPropertyChanged(nameof(ObsDockStateText));
            OnPropertyChanged(nameof(ServerToggleText));
            OnPropertyChanged(nameof(IsServerRunning));
            RefreshStatus();
        }

        [RelayCommand]
        private void RemoveFromObs()
        {
            var result = _dock.RemoveDockFromObs();
            SetObsStatus(result.Message, result.Success);
            OnPropertyChanged(nameof(ObsDockStateText));
        }

        [RelayCommand]
        private void SelectAll()
        {
            foreach (var item in InputChannels.Concat(OutputChannels)) item.IsSelected = true;
        }

        [RelayCommand]
        private void SelectNone()
        {
            foreach (var item in InputChannels.Concat(OutputChannels)) item.IsSelected = false;
        }

        partial void OnShowAllInputsChanged(bool value) => OnPropertyChanged(nameof(InputsGroupEnabled));

        partial void OnShowAllOutputsChanged(bool value) => OnPropertyChanged(nameof(OutputsGroupEnabled));

        /// <summary>false — список входов не влияет на док, пока включены «все входы».</summary>
        public bool InputsGroupEnabled => !ShowAllInputs;

        public bool OutputsGroupEnabled => !ShowAllOutputs;

        private ObsDockSettings BuildSettings(int port) => new()
        {
            Enabled = Enabled,
            Port = port,
            ShowAllInputs = ShowAllInputs,
            ShowAllOutputs = ShowAllOutputs,
            // DeviceId кладём рядом с Id стрипа: движок пересоздаёт стрип с новым Id
            // при каждом повторном обнаружении устройств, и по одному Id выбранный
            // канал молча исчез бы из дока после перезапуска приложения.
            Channels = InputChannels.Where(c => c.IsSelected)
                .Select(c => new ObsDockChannelRef(c.Id, c.DeviceId, ObsDockChannels.Input))
                .Concat(OutputChannels.Where(c => c.IsSelected)
                    .Select(c => new ObsDockChannelRef(c.Id, c.DeviceId, ObsDockChannels.Output)))
                .ToList()
        };

        private bool TryReadPort(out int port)
        {
            return int.TryParse(PortText, out port) && port is >= 1024 and <= 65535;
        }

        private void MarkSelected(ObsDockSettings settings)
        {
            var ids = new HashSet<string>(settings.Channels.Select(c => c.StripId), StringComparer.Ordinal);
            var devices = new HashSet<string>(
                settings.Channels.Select(c => c.DeviceId).Where(d => !string.IsNullOrEmpty(d)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in InputChannels.Concat(OutputChannels))
                item.IsSelected = ids.Contains(item.Id) || (!string.IsNullOrEmpty(item.DeviceId) && devices.Contains(item.DeviceId));
        }

        private void SetObsStatus(string text, bool ok)
        {
            ObsStatus = text;
            IsStatusOk = ok;
        }

        private void RefreshStatus()
        {
            int clients = _dock.DockClients;
            string server = _dock.IsDockRunning
                ? Loc.Get("Sm.Obs.Status.ServerOn", _dock.DockServer.Port)
                : Loc.Get("Sm.Obs.Status.ServerOff");
            string panels = clients == 0
                ? Loc.Get("Sm.Obs.Status.NoPanels")
                : Loc.Get("Sm.Obs.Status.PanelsCount", clients);
            Status = _dock.DockError.Length > 0 ? _dock.DockError : $"{server}, {panels}";
        }

        protected override void OnLanguageChangedCore()
        {
            OnPropertyChanged(nameof(ServerToggleText));
            OnPropertyChanged(nameof(ObsDockStateText));
            RefreshStatus();
        }

        protected override void DisposeCore() => _dock.DockStatusChanged -= RefreshStatus;
    }
}
