using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Выходная шина (стрип вывода): громкость, мут, моно, соло, VU-метр.
/// </summary>
public partial class OutputBusViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly Action _markDirty;
    private string _renameOriginal = "";

    public OutputBusModel Model { get; }
    public string Id => Model.Id;

    [ObservableProperty]
    private string _name;

    /// <summary>Своё имя канала. Пустое — показывается имя устройства (Name).</summary>
    [ObservableProperty]
    private string _channelName;

    /// <summary>true — идёт редактирование имени (inline TextBox вверху стрипа).</summary>
    [ObservableProperty]
    private bool _isRenaming;

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
    private bool _isAvailable;

    [ObservableProperty]
    private float _peakLevel;

    public OutputBusViewModel(IAudioEngine engine, OutputBusModel model, Action markDirty)
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
        _isAvailable = model.IsAvailable;
    }

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
        _engine.SetOutputSolo(Model.Id, value);
        _markDirty();
    }

    public void UpdatePeak(float raw)
    {
        float decayed = PeakLevel * 0.8f;
        float displayed = MathF.Max(raw, decayed);
        PeakLevel = displayed > 0.002f ? displayed : 0f;
    }
}