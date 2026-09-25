using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Ячейка параметра стрипа для MIDI-окна: метка, текущая привязка, Learn/Clear.
/// </summary>
public partial class MidiParamCell : ObservableObject
{
    private bool _refreshing;

    public required MidiParameterDescriptor Descriptor { get; init; }
    public required MidiBindingsViewModel Owner { get; init; }
    public required string TargetType { get; init; }
    public required string StripId { get; init; }

    /// <summary>true — кнопку можно повесить как PTT (кнопочный параметр входного стрипа).</summary>
    public bool IsMomentaryCapable =>
        Descriptor.Shape == MidiParamShape.Button && TargetType == "Input";

    /// <summary>Галочка PTT: привязка работает по удержанию, а не переключением.</summary>
    [ObservableProperty]
    private bool _ptt;

    [ObservableProperty]
    private string _display = "unbound";

    [ObservableProperty]
    private bool _isBound;

    [ObservableProperty]
    private bool _isLearning;

    [RelayCommand]
    private void Learn() => Owner.BeginLearn(this);

    [RelayCommand]
    private void Clear() => Owner.ClearBinding(this);

    partial void OnPttChanged(bool value)
    {
        if (!_refreshing) Owner.SetMomentary(this, value);
    }

    public void Refresh()
    {
        var binding = Owner.FindBinding(TargetType, StripId, Descriptor.Key);
        IsBound = binding != null;
        Display = binding == null ? "unbound" : FormatBinding(binding);
        _refreshing = true;
        Ptt = binding?.IsMomentary ?? false;
        _refreshing = false;
    }

    private static string FormatBinding(MidiBinding b) => b.MessageKind switch
    {
        MidiMessageKind.ControlChange => $"CC {b.Control} · ch {b.Channel + 1}",
        MidiMessageKind.NoteOn => $"Note {b.Control} · ch {b.Channel + 1}",
        MidiMessageKind.NoteOff => $"NoteOff {b.Control} · ch {b.Channel + 1}",
        MidiMessageKind.PitchWheel => "Pitch Wheel",
        _ => "?"
    };

    partial void OnIsLearningChanged(bool value)
    {
        if (value) Display = "listening…";
        else Refresh();
    }
}

/// <summary>
/// Строка стрипа в MIDI-окне: заголовок + список ячеек параметров.
/// </summary>
public class MidiStripRow
{
    public required string Title { get; init; }
    public required string TargetType { get; init; }
    public required string StripId { get; init; }
    public ObservableCollection<MidiParamCell> Params { get; } = new();
}

/// <summary>
/// VM окна MIDI-привязок: устройства, стрипы с параметрами, режим Learn.
/// </summary>
public partial class MidiBindingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private MidiParamCell? _learning;

    public MidiBindingsViewModel(MainViewModel main)
    {
        _main = main;
        Devices = new ObservableCollection<MidiInputDevice> { NoneDevice };
        foreach (var device in main.MidiService.Devices)
            Devices.Add(device);
        _selectedDeviceName = main.Engine.Midi.DeviceName;

        foreach (var input in main.Inputs)
        {
            var row = new MidiStripRow
            {
                Title = input.Title,
                TargetType = "Input",
                StripId = input.Id
            };
            foreach (var descriptor in MidiParameters.ForInput())
                row.Params.Add(CreateCell(row, descriptor));
            Strips.Add(row);
        }

        foreach (var bus in main.Buses)
        {
            var row = new MidiStripRow
            {
                Title = bus.Title,
                TargetType = "Bus",
                StripId = bus.Id
            };
            foreach (var descriptor in MidiParameters.ForBus())
                row.Params.Add(CreateCell(row, descriptor));
            Strips.Add(row);
        }

        main.MidiService.MessageReceived += OnRawMessage;
        RefreshAll();
        UpdateStatus();
    }

    public ObservableCollection<MidiInputDevice> Devices { get; }
    public ObservableCollection<MidiStripRow> Strips { get; } = new();

    /// <summary>Псевдоустройство «выключено».</summary>
    private static readonly MidiInputDevice NoneDevice = new(-1, "(none)");

    [ObservableProperty]
    private string? _selectedDeviceName;

    [ObservableProperty]
    private string _status = "";

    public bool IsOpen => _main.MidiService.IsOpen;

    private MidiParamCell CreateCell(MidiStripRow row, MidiParameterDescriptor descriptor) =>
        new()
        {
            Descriptor = descriptor,
            Owner = this,
            TargetType = row.TargetType,
            StripId = row.StripId
        };

    partial void OnSelectedDeviceNameChanged(string? value)
    {
        // Пользователь выбрал «(none)» — явно выключить MIDI.
        if (value == NoneDevice.Name)
        {
            _main.Engine.Midi.DeviceName = null;
            _main.MidiService.Open(null);
            _main.MidiSettingsChanged();
            OnPropertyChanged(nameof(IsOpen));
            UpdateStatus();
            return;
        }

        // value == null приходит от ComboBox, когда сохранённое устройство
        // сейчас не подключено: ComboBox не находит такой элемент и сбрасывает
        // выбор. Сохранённое имя НЕ стираем, чтобы при переподключении
        // контроллера привязка снова заработала.
        if (value == null)
        {
            OnPropertyChanged(nameof(IsOpen));
            UpdateStatus();
            return;
        }

        _main.Engine.Midi.DeviceName = value;
        _main.MidiService.Open(value);
        _main.MidiSettingsChanged();
        RefreshAll();
        UpdateStatus();
        OnPropertyChanged(nameof(IsOpen));
    }

    public MidiBinding? FindBinding(string targetType, string stripId, string parameter) =>
        _main.Engine.Midi.Bindings.FirstOrDefault(b =>
            b.TargetType == targetType && b.StripId == stripId && b.Parameter == parameter);

    public void BeginLearn(MidiParamCell cell)
    {
        foreach (var row in Strips)
            foreach (var p in row.Params)
                p.IsLearning = false;

        _learning = cell;
        cell.IsLearning = true;
        Status = "Move the MIDI control to bind…";
    }

    public void ClearBinding(MidiParamCell cell)
    {
        var bindings = _main.Engine.Midi.Bindings;
        bindings.RemoveAll(b =>
            b.TargetType == cell.TargetType && b.StripId == cell.StripId && b.Parameter == cell.Descriptor.Key);
        _main.MidiSettingsChanged();
        RefreshAll();
        UpdateStatus();
    }

    /// <summary>Галочка PTT: переключаем режим существующей привязки (удержание/переключение).</summary>
    public void SetMomentary(MidiParamCell cell, bool momentary)
    {
        var binding = FindBinding(cell.TargetType, cell.StripId, cell.Descriptor.Key);
        if (binding == null || binding.IsMomentary == momentary) return;
        binding.IsMomentary = momentary;
        _main.MidiSettingsChanged();
    }

    private void OnRawMessage(MidiMessageInfo msg)
    {
        // MIDI-поток: перекидываем на UI-поток (для Learn).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) HandleMessage(msg);
        else dispatcher.BeginInvoke(() => HandleMessage(msg));
    }

    private void HandleMessage(MidiMessageInfo msg)
    {
        if (_learning == null) return;

        var cell = _learning;
        _learning = null;
        cell.IsLearning = false;

        var bindings = _main.Engine.Midi.Bindings;
        bindings.RemoveAll(b =>
            b.TargetType == cell.TargetType && b.StripId == cell.StripId && b.Parameter == cell.Descriptor.Key);

        bindings.Add(new MidiBinding
        {
            TargetType = cell.TargetType,
            StripId = cell.StripId,
            Parameter = cell.Descriptor.Key,
            Channel = msg.Channel,
            Control = msg.Control,
            MessageKind = msg.Kind,
            IsMomentary = cell.Ptt
        });

        _main.MidiSettingsChanged();
        RefreshAll();
        UpdateStatus();
    }

    private void RefreshAll()
    {
        foreach (var row in Strips)
            foreach (var p in row.Params)
                p.Refresh();
    }

    private void UpdateStatus()
    {
        Status = IsOpen
            ? $"Listening: {_main.Engine.Midi.DeviceName}"
            : "No MIDI input selected";
    }

    public void Dispose()
    {
        _main.MidiService.MessageReceived -= OnRawMessage;
    }
}