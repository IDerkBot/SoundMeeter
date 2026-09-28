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
    /// Стрип принимает перенос приложений, только если у него есть render-устройство,
    /// которое снимает микшер: loopback-источник или вход виртуального кабеля.
    /// У микрофона и неназначенного входа принимать нечего — их назначение задаёт
    /// пользователь, и молча подменять его чужим устройством нельзя.
    /// </summary>
    public bool CanAcceptApps => AppSourceDeviceId != null;

    /// <summary>
    /// Render-устройство, в которое уходят приложения стрипа. У стрипа на входе
    /// виртуального кабеля это связанный выход того же кабеля (звук приходит в
    /// вход, а уходит в выход) либо явно выбранный пользователем, у loopback-
    /// стрипа — его собственное устройство, у микрофона — null.
    /// </summary>
    public string? AppSourceDeviceId { get; }

    /// <summary>true — стрип снимает вход виртуального кабеля. Связку кабеля
    /// движок обычно определяет сам, но имена кабелей задаёт пользователь, поэтому
    /// для них оставлен ручной выбор.</summary>
    public bool IsCableCapture { get; }

    /// <summary>true — для этого стрипа имеет смысл указать выход вручную.</summary>
    public bool CanChooseAppTarget => IsCableCapture;

    /// <summary>Куда уходят приложения (или приглашение выбрать, если цели нет).</summary>
    public string AppTargetText =>
        CanChooseAppTarget
            ? $"Приложения → {(AppSourceDeviceId != null ? AppTargetName : "выбрать…")} ▾"
            : "";

    /// <summary>Имя устройства, куда уходят приложения, для подписи в списке.</summary>
    public string AppTargetName { get; }

    public string AppDropHint => CanAcceptApps
        ? IsCableCapture
            ? "Перетащите приложение — оно уйдёт в выход кабеля"
            : "Перетащите приложение сюда"
        : IsCableCapture
            ? "Связанный выход не найден — выберите его ниже"
            : IsMicrophone
                ? "Микрофон приложений не принимает"
                : "Источник не назначен";

    /// <summary>
    /// Объяснение отказа в переносе: приложение уходит в устройство, которое
    /// снимает стрип, а у этого стрипа такого устройства нет.
    /// </summary>
    public string AppRejectReason() => IsCableCapture
        ? $"По имени «{Model.Name}» не удалось определить связанный выход кабеля. " +
          "Укажите его кнопкой «Приложения → …» — это выход того же кабеля " +
          "(у входа «L1In.…» это «L1Out.…»)."
        : IsMicrophone
            ? $"«{Title}» — микрофон: он снимает звук с устройства захвата, а не приложения. " +
              "Перетащите приложение на канал (стрип с loopback-источником) или на вход виртуального кабеля."
            : $"У «{Title}» не назначен источник. Выберите его (клик по имени источника) и перетащите приложение снова.";

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

    public InputChannelViewModel(
        InputChannelModel model,
        IAudioEngine engine,
        IReadOnlyList<OutputBusModel> buses,
        IReadOnlyList<DeviceInfo> catalog,
        Action markDirty)
    {
        Model = model;
        _engine = engine;
        _markDirty = markDirty;

        AppSourceDeviceId = ResolveAppSourceDeviceId(model, catalog);
        IsCableCapture = IsCableCaptureDevice(model, catalog);
        AppTargetName = NameOfDevice(catalog, AppSourceDeviceId);

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
            var route = model.BusRouting.GetValueOrDefault(bus.Id);
            var option = new OutputOptionViewModel(
                bus.Id,
                bus.Name,
                bus.ChannelName,
                route?.Enabled ?? false,
                route?.GainDb ?? 0f,
                OnOptionToggled,
                OnOptionGainChanged,
                OnOptionHidden);
            if (CablePairing.IsVirtualCableName(bus.Name)) VirtualOutputs.Add(option);
            else HardwareOutputs.Add(option);
        }

        RefreshCounts();
    }

    /// <summary>
    /// Обновляет подпись выхода во всех попапах стрипа после переименования
    /// канала на стрипе выхода: показываем имя канала, а не устройства.
    /// </summary>
    public void UpdateBusChannelName(string busId, string? channelName)
    {
        foreach (var option in HardwareOutputs)
            if (option.BusId == busId) option.SetChannelName(channelName);
        foreach (var option in VirtualOutputs)
            if (option.BusId == busId) option.SetChannelName(channelName);
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
    /// Индивидуальная посылка входа в эту шину. Применяется на лету: тап читает
    /// GainDb из живого объекта маршрута, пересоздавать аудиопоток не нужно.
    /// </summary>
    private void OnOptionGainChanged(OutputOptionViewModel option, float gainDb)
    {
        _engine.SetRouteGain(Model.Id, option.BusId, gainDb);
        _markDirty();
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

    /// <summary>
    /// Куда направить приложения, перенесённые на этот стрип. Половинки виртуального
    /// кабеля связны движком: вход кабеля снимаем мы, а играть приложения должны
    /// в его выход — иначе приложения уйдут в реальные колонки мимо кабеля.
    /// </summary>
    private static string? ResolveAppSourceDeviceId(
        InputChannelModel model,
        IReadOnlyList<DeviceInfo> catalog) =>
        CablePairing.GetAppRenderDeviceId(
            FindDevice(model, catalog), model.IsMicrophone, model.AppTargetDeviceId);

    /// <summary>true — устройство стрипа это вход виртуального кабеля: связку кабеля
    /// движок обычно определяет сам, но имена кабелей задаёт пользователь, поэтому
    /// для них оставлен ручной выбор.</summary>
    private static bool IsCableCaptureDevice(InputChannelModel model, IReadOnlyList<DeviceInfo> catalog) =>
        CablePairing.IsCableCapture(FindDevice(model, catalog));

    /// <summary>Имя устройства, куда уходят приложения, для подписи в списке.
    /// Хвост в скобках (имя драйвера) убираем — в панели стрипа текст обрезается,
    /// а «(Virtual Audio Cable)» пользователю ничего не говорит.</summary>
    private static string NameOfDevice(IReadOnlyList<DeviceInfo> catalog, string? deviceId)
    {
        if (deviceId == null) return "";

        var name = catalog.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? deviceId;

        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    /// <summary>Запись каталога для источника стрипа; null, если источник не назначен
    /// или устройства больше нет в системе.</summary>
    private static DeviceInfo? FindDevice(InputChannelModel model, IReadOnlyList<DeviceInfo> catalog) =>
        string.IsNullOrWhiteSpace(model.DeviceId)
            ? null
            : catalog.FirstOrDefault(d => string.Equals(d.DeviceId, model.DeviceId, StringComparison.OrdinalIgnoreCase));

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
