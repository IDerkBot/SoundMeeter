using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Входной стрип (микрофон/loopback): громкость, мут, моно, соло, VU-метр
/// и два списка выходов — аппаратные (OUT, для прослушивания) и виртуальные (VIRT).
/// </summary>
public partial class InputChannelViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly Action _markDirty;
    private string _renameOriginal = "";

    public InputChannelModel Model { get; }
    public string Id => Model.Id;
    public bool IsMicrophone => Model.IsMicrophone;

    /// <summary>
    /// Стрип принимает перенос приложений. Если стрип ещё не снимает render-устройство
    /// (микрофон или пустой источник), он при этом автоматически цепляется к текущему
    /// выходу приложения через loopback.
    /// </summary>
    public bool CanAcceptApps => !string.IsNullOrWhiteSpace(Id);

    /// <summary>true — стрип уже снимает render-устройство, то есть это канал приложений.</summary>
    public bool HasAppSource => !IsMicrophone && Model.IsAvailable && !string.IsNullOrWhiteSpace(Model.DeviceId);

    public string AppDropHint => HasAppSource
        ? "Перетащите приложение сюда"
        : "Перетащите приложение — источником станет его текущий выход";

    [ObservableProperty]
    private string _name;

    /// <summary>Своё имя канала. Пустое — показывается имя устройства (Name).</summary>
    [ObservableProperty]
    private string _channelName;

    /// <summary>true — идёт редактирование имени (inline TextBox вверху стрипа).</summary>
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Имя, отображаемое сверху стрипа (ChannelName или имя устройства).</summary>
    public string Title => string.IsNullOrWhiteSpace(ChannelName) ? Name : ChannelName;

    public void BeginRename()
    {
        _renameOriginal = ChannelName;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        ChannelName = ChannelName?.Trim() ?? "";
        IsRenaming = false;
    }

    public void CancelRename()
    {
        ChannelName = _renameOriginal ?? "";
        IsRenaming = false;
    }

    [ObservableProperty]
    private float _volumeDb;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isMono;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private float _peakLevel;

    [ObservableProperty]
    private bool _denoiserEnabled;

    [ObservableProperty]
    private float _denoiserNoiseRemover;

    [ObservableProperty]
    private float _denoiserDryWet;

    [ObservableProperty]
    private float _denoiserFormantLowDb;

    [ObservableProperty]
    private float _denoiserFormantMidDb;

    [ObservableProperty]
    private float _denoiserFormantHighDb;

    [ObservableProperty]
    private float _denoiserFormantGroupDb;

    public ObservableCollection<OutputOptionViewModel> HardwareOutputs { get; } = new();
    public ObservableCollection<OutputOptionViewModel> VirtualOutputs { get; } = new();

    /// <summary>Запущенные приложения, перенаправленные на этот стрип (канал).</summary>
    public ObservableCollection<AppViewModel> AssignedApps { get; } = new();

    /// <summary>Постоянно назначенные (persistent) приложения этого стрипа (канала).</summary>
    public ObservableCollection<ConfiguredAppViewModel> ConfiguredApps { get; } = new();

    public InputChannelViewModel(InputChannelModel model, IAudioEngine engine, IReadOnlyList<OutputBusModel> buses, Action markDirty)
    {
        Model = model;
        _engine = engine;
        _markDirty = markDirty;

        _name = model.Name;
        _channelName = model.ChannelName;
        _volumeDb = model.VolumeDb;
        _isMuted = model.IsMuted;
        _isMono = model.IsMono;
        _isSolo = model.IsSolo;
        _denoiserEnabled = model.DenoiserEnabled;
        _denoiserNoiseRemover = model.DenoiserNoiseRemover;
        _denoiserDryWet = model.DenoiserDryWet;
        _denoiserFormantLowDb = model.DenoiserFormantLowDb;
        _denoiserFormantMidDb = model.DenoiserFormantMidDb;
        _denoiserFormantHighDb = model.DenoiserFormantHighDb;
        _denoiserFormantGroupDb = model.DenoiserFormantGroupDb;

        foreach (var bus in buses)
        {
            var enabled = model.BusRouting.GetValueOrDefault(bus.Id)?.Enabled ?? false;
            var option = new OutputOptionViewModel(bus.Id, bus.Name, enabled, OnOptionToggled, OnOptionHidden);
            if (IsVirtualCable(bus.Name)) VirtualOutputs.Add(option);
            else HardwareOutputs.Add(option);
        }

        RefreshCounts();
    }

    public string OutButtonText => $"OUT {HardwareOutputs.Count(o => o.IsEnabled)}/{HardwareOutputs.Count} ▾";
    public string VirtButtonText => $"VIRT {VirtualOutputs.Count(o => o.IsEnabled)}/{VirtualOutputs.Count} ▾";
    public bool HasHardware => HardwareOutputs.Count > 0;
    public bool HasVirtual => VirtualOutputs.Count > 0;

    partial void OnChannelNameChanged(string value)
    {
        Model.ChannelName = value ?? "";
        OnPropertyChanged(nameof(Title));
        _markDirty();
    }

    partial void OnVolumeDbChanged(float value)
    {
        Model.VolumeDb = value;
        _markDirty();
    }

    partial void OnIsMutedChanged(bool value)
    {
        Model.IsMuted = value;
        _markDirty();
    }

    partial void OnIsMonoChanged(bool value)
    {
        Model.IsMono = value;
        _markDirty();
    }

    partial void OnIsSoloChanged(bool value)
    {
        _engine.SetInputSolo(Model.Id, value);
        _markDirty();
    }

    partial void OnDenoiserEnabledChanged(bool value)
    {
        Model.DenoiserEnabled = value;
        _markDirty();
    }

    partial void OnDenoiserNoiseRemoverChanged(float value)
    {
        Model.DenoiserNoiseRemover = value;
        _markDirty();
    }

    partial void OnDenoiserDryWetChanged(float value)
    {
        Model.DenoiserDryWet = value;
        _markDirty();
    }

    partial void OnDenoiserFormantLowDbChanged(float value)
    {
        Model.DenoiserFormantLowDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantMidDbChanged(float value)
    {
        Model.DenoiserFormantMidDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantHighDbChanged(float value)
    {
        Model.DenoiserFormantHighDb = value;
        _markDirty();
    }

    partial void OnDenoiserFormantGroupDbChanged(float value)
    {
        Model.DenoiserFormantGroupDb = value;
        _markDirty();
    }

    private void OnOptionToggled(OutputOptionViewModel option)
    {
        _engine.SetRoute(Model.Id, option.BusId, option.IsEnabled);
        _markDirty();
        RefreshCounts();
    }

    /// <summary>
    /// Скрыть устройство из попапов OUT/VIRT: удаляет стрип шины и помечает
    /// устройство как скрытое (движок больше не будет его пересоздавать).
    /// </summary>
    private void OnOptionHidden(OutputOptionViewModel option)
    {
        _engine.RemoveBus(option.BusId);
        _markDirty();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(OutButtonText));
        OnPropertyChanged(nameof(VirtButtonText));
        OnPropertyChanged(nameof(HasHardware));
        OnPropertyChanged(nameof(HasVirtual));
    }

    private static bool IsVirtualCable(string busName)
    {
        return busName.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
               busName.Contains("vb-audio", StringComparison.OrdinalIgnoreCase) ||
               busName.Contains("virtual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Обновляет пик для VU-метра (вызывается из UI-таймера). Экспоненциальный спад.
    /// </summary>
    public void UpdatePeak(float raw)
    {
        float decayed = PeakLevel * 0.8f;
        float displayed = MathF.Max(raw, decayed);
        PeakLevel = displayed > 0.002f ? displayed : 0f;
    }
}
