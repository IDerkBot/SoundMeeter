using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Audio.Contract;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;
using SoundMeeter.Midi.Presentation.Dto;
using SoundMeeter.Midi.Presentation.Helpers;
using SoundMeeter.Midi.UseCases;
using System.Collections.ObjectModel;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Midi.Presentation.ViewModels;

/// <summary>
/// VM окна MIDI-привязок: устройства, стрипы с параметрами, режим Learn.
///
/// Правило слоя: здесь нет ни одной строки, которая меняет настройки или
/// обрабатывает MIDI-сообщение. Всё, что можно испортить, делается сценарием из
/// <c>SoundMeeter.Midi.UseCases</c>, а VM перерисовывается по его результату.
/// Раньше это правило не выдерживалось: окно само лезло в
/// <c>Engine.Midi.Bindings</c>, добавляло и удаляло записи, и правило режима
/// кнопки меняло прямо в доменном объекте — минуя любую проверку.
///
/// Исключение одно, и оно вынужденное: Learn ждёт СЛЕДУЮЩЕЕ сообщение
/// контроллера, а привязку создаёт из него сценарий. Слушать порт второй раз
/// ради одного окна нельзя — вторая подписка обрабатывала бы сообщения ещё раз
/// и переключала бы кнопки дважды за одно нажатие.
/// </summary>
public partial class MidiBindingsViewModel : LocalizedViewModel
{
    private readonly IMidiService _midi;
    private readonly IDispatcherService _dispatcherService;
    private readonly SelectMidiDeviceUseCase _selectDevice;
    private readonly BindMidiControlUseCase _bindControl;
    private readonly UpdateMidiBindingUseCase _updateBinding;
    private readonly ClearMidiBindingUseCase _clearBinding;
    private readonly GetMidiBindingQuery _getMidiBinding;
    private readonly IStripProvider _stripProvider;

    private MidiParamCell? _learning;

    public MidiBindingsViewModel(
        IMidiService midi,
        IDispatcherService dispatcherService,
        SelectMidiDeviceUseCase selectDevice,
        BindMidiControlUseCase bindControl,
        UpdateMidiBindingUseCase updateBinding,
        ClearMidiBindingUseCase clearBinding,
        GetMidiBindingQuery getMidiBinding,
        IStripProvider stripProvider)
    {
        _midi = midi;
        _dispatcherService = dispatcherService;
        _selectDevice = selectDevice;
        _bindControl = bindControl;
        _updateBinding = updateBinding;
        _clearBinding = clearBinding;
        _getMidiBinding = getMidiBinding;
        _stripProvider = stripProvider;

        foreach (var device in _selectDevice.GetAvailableDevices())
            DeviceOptions.Add(new MidiDeviceOption(device));

        _selectedDevice = FindOption(_selectDevice.ResolveSavedDevice(DeviceOptionsSnapshot));

        foreach (var input in _stripProvider.Inputs)
        {
            var row = new MidiStripRow
            {
                Title = input.Title,
                TargetType = MidiTargetTypes.Input,
                StripId = input.Id
            };
            foreach (var descriptor in MidiParameters.ForInput())
                row.Params.Add(CreateCell(row, descriptor));
            Strips.Add(row);
        }

        foreach (var bus in _stripProvider.Buses)
        {
            var row = new MidiStripRow
            {
                Title = bus.Title,
                TargetType = MidiTargetTypes.Bus,
                StripId = bus.Id
            };
            foreach (var descriptor in MidiParameters.ForBus())
                row.Params.Add(CreateCell(row, descriptor));
            Strips.Add(row);
        }

        _midi.MessageReceived += OnRawMessage;
        RefreshAll();
        UpdateStatus();
    }

    public ObservableCollection<MidiDeviceOption> DeviceOptions { get; } = new();
    public ObservableCollection<MidiStripRow> Strips { get; } = new();

    [ObservableProperty]
    private MidiDeviceOption? _selectedDevice;

    [ObservableProperty]
    private string _status = "";

    public bool IsOpen => _midi.IsOpen;

    /// <summary>
    /// Список для правила выбора устройства. <see cref="MidiDeviceDto.Resolve"/>
    /// принимает коллекцию DTO, а UI работает с обёртками для ComboBox, поэтому
    /// список пересобирается на каждый вызов — он короткий и живёт только на
    /// время открытия окна.
    /// </summary>
    private IReadOnlyList<MidiDeviceDto> DeviceOptionsSnapshot =>
        DeviceOptions.Select(o => o.Value).ToList();

    /// <summary>
    /// Пункт по имени устройства. Правило «сохранённое устройство отключено →
    /// показать выключено, но имя не стирать» живёт в сценарии, здесь только
    /// подстановка найденного пункта в UI.
    /// </summary>
    private MidiDeviceOption FindOption(MidiDeviceDto saved)
    {
        foreach (var option in DeviceOptions)
        {
            if (option.Value.Device is not null &&
                option.Value.Device.Name == saved.Name)
                return option;
        }

        return DeviceOptions[0];
    }

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

        var result = _selectDevice.Execute(value.IsNone ? null : value.Name);

        RefreshAll();
        OnPropertyChanged(nameof(IsOpen));

        // Ошибку показываем текстом сценария, а не молча пишем «слушаем».
        // Сценарий различает «устройства нет» и «не открылось» — раньше оба
        // случая выглядели в UI одинаково успешными.
        Status = result.Selection.IsSuccess
            ? IsOpen
                ? Loc.Get("Sm.Midi.Status.ListeningTo", result.DeviceName)
                : Loc.Get("Sm.Midi.Status.NoInput")
            : Loc.Get(result.MessageKey);
    }

    /// <summary>Текущая привязка параметра — для показа в ячейке.</summary>
    public MidiBindingDto? FindBinding(string targetType, string stripId, string parameter)
    {
        var record = _getMidiBinding.Execute(targetType, stripId, parameter);
        return record is null ? null : MidiBindingDto.From(record);
    }

    public void BeginLearn(MidiParamCell cell)
    {
        foreach (var row in Strips)
            foreach (var p in row.Params)
                p.IsLearning = false;

        _learning = cell;
        cell.IsLearning = true;
        Status = Loc.Get("Sm.Midi.Status.MoveControl");
    }

    public void ClearBinding(MidiParamCell cell) =>
        RunAndRefresh(cell, () => _clearBinding.Execute(cell.TargetType, cell.StripId, cell.Descriptor.Key));

    /// <summary>Режим кнопки: переключение / удержание (PTT) / кнопка с фиксацией.</summary>
    public void SetMode(MidiParamCell cell, MidiButtonMode mode) =>
        RunAndRefresh(cell, () => _updateBinding.SetMode(cell.TargetType, cell.StripId, cell.Descriptor.Key, mode));

    /// <summary>Обратная полярность CC: 0 = нажато, 127 = отпущено.</summary>
    public void SetInverted(MidiParamCell cell, bool inverted) =>
        RunAndRefresh(cell, () => _updateBinding.SetInverted(cell.TargetType, cell.StripId, cell.Descriptor.Key, inverted));

    /// <summary>
    /// Выполнить сценарий и, если он что-то изменил, перерисовать ячейки.
    /// Перерисовка нужна всем: смена режима кнопки меняет и её подпись.
    /// </summary>
    private void RunAndRefresh(MidiParamCell cell, Func<MidiResult> action)
    {
        var result = action();

        if (!result.IsSuccess)
            Status = Loc.Get(result.MessageKey);

        RefreshAll();
    }

    private void OnRawMessage(MidiMessageInfo msg)
    {
        // MIDI-поток: перекидываем на UI-поток (для Learn).
        if (_dispatcherService.HasThreadAccess) HandleMessage(msg);
        else _dispatcherService.Post(() => HandleMessage(msg));
    }

    /// <summary>
    /// Первое сообщение после нажатия «Learn» становится привязкой.
    /// </summary>
    private void HandleMessage(MidiMessageInfo msg)
    {
        var cell = _learning;
        if (cell is null) return;

        _learning = null;
        cell.IsLearning = false;

        var result = _bindControl.Execute(new MidiBindingCommand(
            cell.TargetType,
            cell.StripId,
            cell.Descriptor.Key,
            msg.Channel,
            msg.Control,
            msg.Kind,
            cell.Mode,
            cell.IsInverted));

        RefreshAll();

        if (result.IsSuccess) UpdateStatus();
        else Status = Loc.Get(result.MessageKey);
    }

    private void RefreshAll()
    {
        foreach (var row in Strips)
            foreach (var p in row.Params)
                p.Refresh();
    }

    private void UpdateStatus() =>
        Status = IsOpen
            ? Loc.Get("Sm.Midi.Status.ListeningTo", _getMidiBinding.DeviceName)
            : Loc.Get("Sm.Midi.Status.NoInput");

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
        _midi.MessageReceived -= OnRawMessage;
        foreach (var option in DeviceOptions) option.Dispose();
        foreach (var row in Strips)
            foreach (var cell in row.Params)
                cell.Dispose();
    }
}