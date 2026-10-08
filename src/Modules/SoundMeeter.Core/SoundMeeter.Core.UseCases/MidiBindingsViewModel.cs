using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Пункт выпадающего списка режима кнопки. Название берётся из ресурсов по ключу,
/// поэтому переключение языка обновляет и его (список пересоздаётся заново).
/// </summary>
public sealed class MidiButtonModeOption
{
    public MidiButtonModeOption(MidiButtonMode value, string nameKey)
    {
        Value = value;
        NameKey = nameKey;
    }

    public MidiButtonMode Value { get; }

    public string NameKey { get; }

    public string Name => Loc.Get(NameKey);

    public override string ToString() => Name;
}

/// <summary>
/// Ячейка параметра стрипа для MIDI-окна: метка, текущая привязка, Learn/Clear.
/// </summary>
public partial class MidiParamCell : LocalizedViewModel
{
    private bool _refreshing;

    private static readonly MidiButtonModeOption ToggleMode =
        new(MidiButtonMode.Toggle, "Sm.Midi.Mode.Toggle");
    private static readonly MidiButtonModeOption HoldMode =
        new(MidiButtonMode.Hold, "Sm.Midi.Mode.Hold");
    private static readonly MidiButtonModeOption LatchMode =
        new(MidiButtonMode.Latch, "Sm.Midi.Mode.Latch");

    public required MidiParameterDescriptor Descriptor { get; init; }
    public required MidiBindingsViewModel Owner { get; init; }
    public required string TargetType { get; init; }
    public required string StripId { get; init; }

    /// <summary>Параметр — кнопка: доступны режим работы и обратная полярность.</summary>
    public bool IsButton => Descriptor.Shape == MidiParamShape.Button;

    /// <summary>
    /// Режимы кнопки: переключение, удержание (PTT), кнопка с фиксацией.
    /// Новый список на каждое чтение — иначе ComboBox не перечитал бы названия
    /// после смены языка.
    /// </summary>
    public IReadOnlyList<MidiButtonModeOption> ModeOptions => new[] { ToggleMode, HoldMode, LatchMode };

    /// <summary>Режим кнопки: Toggle / Hold (PTT) / Latch (кнопка с фиксацией).</summary>
    [ObservableProperty]
    private MidiButtonMode _mode = MidiButtonMode.Toggle;

    /// <summary>true — обратная полярность CC (0 = нажато, 127 = отпущено).</summary>
    [ObservableProperty]
    private bool _isInverted;

    [ObservableProperty]
    private string _display = "";

    [ObservableProperty]
    private bool _isBound;

    [ObservableProperty]
    private bool _isLearning;

    [RelayCommand]
    private void Learn() => Owner.BeginLearn(this);

    [RelayCommand]
    private void Clear() => Owner.ClearBinding(this);

    partial void OnModeChanged(MidiButtonMode value)
    {
        if (!_refreshing) Owner.SetMode(this, value);
    }

    partial void OnIsInvertedChanged(bool value)
    {
        if (!_refreshing) Owner.SetInverted(this, value);
    }

    public void Refresh()
    {
        var binding = Owner.FindBinding(TargetType, StripId, Descriptor.Key);
        IsBound = binding != null;
        Display = binding == null ? Loc.Get("Sm.Midi.Unbound") : FormatBinding(binding);
        _refreshing = true;
        Mode = binding?.Mode ?? MidiButtonMode.Toggle;
        IsInverted = binding?.IsInverted ?? false;
        _refreshing = false;
    }

    private static string FormatBinding(MidiBinding b) => b.MessageKind switch
    {
        MidiMessageKind.ControlChange => Loc.Get("Sm.Midi.Binding.ControlChange", b.Control, b.Channel + 1),
        MidiMessageKind.NoteOn => Loc.Get("Sm.Midi.Binding.NoteOn", b.Control, b.Channel + 1),
        MidiMessageKind.NoteOff => Loc.Get("Sm.Midi.Binding.NoteOff", b.Control, b.Channel + 1),
        MidiMessageKind.PitchWheel => Loc.Get("Sm.Midi.Binding.PitchWheel"),
        _ => "?"
    };

    partial void OnIsLearningChanged(bool value)
    {
        if (value) Display = Loc.Get("Sm.Midi.Listening");
        else Refresh();
    }
}

/// <summary>
/// Пункт выпадающего списка MIDI-устройств. Реальные устройства хранят имя
/// из системы, а «выключено» — локализованную надпись; выбор идёт по ссылке на
/// объект, поэтому смена языка не может его сбросить (сравнение по имени, как
/// было раньше, роняло выбор при переключении языка).
/// </summary>
public sealed class MidiDeviceOption : LocalizedViewModel
{
    /// <summary>Псевдоустройство «выключено»: <paramref name="device"/> — null.</summary>
    public MidiDeviceOption(MidiInputDevice? device) => Device = device;

    public MidiInputDevice? Device { get; }

    public bool IsNone => Device is null;

    public string Name => Device?.Name ?? Loc.Get("Sm.Midi.None");

    public override string ToString() => Name;
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
public partial class MidiBindingsViewModel : LocalizedViewModel
{
    private readonly MainViewModel _main;
    private readonly IDispatcherService _dispatcherService;
    private MidiParamCell? _learning;

    public MidiBindingsViewModel(MainViewModel main, IDispatcherService dispatcherService)
    {
        _main = main;
        _dispatcherService = dispatcherService;

        DeviceOptions.Add(new MidiDeviceOption(null));
        foreach (var device in main.MidiService.Devices)
            DeviceOptions.Add(new MidiDeviceOption(device));

        _selectedDevice = FindOption(main.Engine.Midi.DeviceName);

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

    public ObservableCollection<MidiDeviceOption> DeviceOptions { get; } = new();
    public ObservableCollection<MidiStripRow> Strips { get; } = new();

    [ObservableProperty]
    private MidiDeviceOption? _selectedDevice;

    [ObservableProperty]
    private string _status = "";

    public bool IsOpen => _main.MidiService.IsOpen;

    /// <summary>
    /// Пункт по имени устройства. null — сохранённое устройство сейчас не
    /// подключено: показываем «выключено», но имя НЕ стираем, чтобы при
    /// переподключении контроллера привязка снова заработала.
    /// </summary>
    private MidiDeviceOption FindOption(string? deviceName) =>
        DeviceOptions.FirstOrDefault(o => o.Device is not null &&
            string.Equals(o.Device.Name, deviceName, StringComparison.Ordinal))
        ?? DeviceOptions[0];

    private MidiParamCell CreateCell(MidiStripRow row, MidiParameterDescriptor descriptor) =>
        new()
        {
            Descriptor = descriptor,
            Owner = this,
            TargetType = row.TargetType,
            StripId = row.StripId
        };

    partial void OnSelectedDeviceChanged(MidiDeviceOption? value)
    {
        if (value is null) return;

        // Пользователь выбрал «выключено» — явно закрыть MIDI.
        if (value.IsNone)
        {
            _main.Engine.Midi.DeviceName = null;
            _main.MidiService.Close();
            _main.MidiSettingsChanged();
            OnPropertyChanged(nameof(IsOpen));
            UpdateStatus();
            return;
        }

        var name = value.Device!.Name;
        _main.Engine.Midi.DeviceName = name;
        _main.MidiService.Open(name);
        // MidiSettingsChanged сбрасывает и отслеживание нажатий: устройство сменилось.
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
        Status = Loc.Get("Sm.Midi.Status.MoveControl");
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

    /// <summary>Режим кнопки: переключение / удержание (PTT) / кнопка с фиксацией.</summary>
    public void SetMode(MidiParamCell cell, MidiButtonMode mode)
    {
        var binding = FindBinding(cell.TargetType, cell.StripId, cell.Descriptor.Key);
        if (binding == null || binding.Mode == mode) return;
        binding.Mode = mode;
        _main.MidiSettingsChanged();
    }

    /// <summary>Обратная полярность CC: 0 = нажато, 127 = отпущено.</summary>
    public void SetInverted(MidiParamCell cell, bool inverted)
    {
        var binding = FindBinding(cell.TargetType, cell.StripId, cell.Descriptor.Key);
        if (binding == null || binding.IsInverted == inverted) return;
        binding.IsInverted = inverted;
        _main.MidiSettingsChanged();
    }

    private void OnRawMessage(MidiMessageInfo msg)
    {
        // MIDI-поток: перекидываем на UI-поток (для Learn).
        if (_dispatcherService.HasThreadAccess) HandleMessage(msg);
        else _dispatcherService.Post(() => HandleMessage(msg));
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
            Mode = cell.Mode,
            IsInverted = cell.IsInverted
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
            ? Loc.Get("Sm.Midi.Status.ListeningTo", _main.Engine.Midi.DeviceName)
            : Loc.Get("Sm.Midi.Status.NoInput");
    }

    /// <summary>
    /// Смена языка: надписи устройств и ячеек обновит базовый класс, а статус
    /// и название параметров считаются здесь и в дескрипторах — пересчитываем явно.
    /// </summary>
    protected override void OnLanguageChangedCore()
    {
        foreach (var row in Strips)
            foreach (var cell in row.Params)
                cell.Refresh();
        UpdateStatus();
    }

    protected override void DisposeCore()
    {
        _main.MidiService.MessageReceived -= OnRawMessage;
        foreach (var option in DeviceOptions) option.Dispose();
        foreach (var row in Strips)
            foreach (var cell in row.Params)
                cell.Dispose();
    }
}