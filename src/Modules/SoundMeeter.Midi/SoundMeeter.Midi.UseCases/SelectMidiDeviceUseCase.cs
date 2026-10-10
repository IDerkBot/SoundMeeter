using Microsoft.Extensions.Logging;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Итог выбора MIDI-устройства: чем закончилось и что теперь слушает модуль.
/// </summary>
public sealed record SelectMidiDeviceResult(MidiResult<MidiDeviceDto> Selection)
{
    public bool IsOpen => Selection.IsSuccess && Selection.Value.Device is not null;

    /// <summary>Имя сохранённого устройства (null — MIDI выключен).</summary>
    public string? DeviceName => Selection.IsSuccess ? Selection.Value.Name : null;

    public string MessageKey => Selection.MessageKey;
}

/// <summary>
/// Подключает модуль к MIDI-устройству или отключает его.
///
/// Сценарий состоит из трёх шагов, и порядок нельзя менять: сначала проверяем,
/// что устройство вообще есть, потом пишем имя в настройки, потом открываем порт.
/// Проверка идёт первая, потому что <see cref="IMidiService.Open"/> возвращает
/// <c>void</c> и проглатывает исключение, проглотив его в журнал — вызывающий
/// узнал бы «открыто» даже когда устройство занято другим приложением, и UI
/// написал бы «слушаем», а MIDI молчал бы. Сверка <c>Devices</c> ДО вызова
/// отделяет «устройства нет» от «открыть не вышло» без правки контракта порта.
///
/// Имя пишется в настройки, даже если порт не открылся. Это не опечатка:
/// контроллер часто включают позже приложения, и сохранив имя, мы получим
/// рабочую привязку без ручного выбора, как только устройство появится.
/// Обратное поведение (стереть имя при неудаче) уводило бы привязки в никуда
/// после одного переподключения.
///
/// Откат зажатых PTT обязателен: сменилось устройство — события от старого
/// больше не придут, и удержание осталось бы включённым навсегда.
/// </summary>
public sealed class SelectMidiDeviceUseCase
{
    private readonly IMidiService _midi;
    private readonly IMidiBindingStore _bindings;
    private readonly ResetMidiTrackingUseCase _resetTracking;
    private readonly ILogger _logger = AppLog.For<SelectMidiDeviceUseCase>();

    public SelectMidiDeviceUseCase(
        IMidiService midi,
        IMidiBindingStore bindings,
        ResetMidiTrackingUseCase resetTracking)
    {
        _midi = midi;
        _bindings = bindings;
        _resetTracking = resetTracking;
    }

    /// <summary>
    /// Открывает устройство по имени. <paramref name="deviceName"/> = null —
    /// закрыть MIDI.
    /// </summary>
    public SelectMidiDeviceResult Execute(string? deviceName)
    {
        // Устройство сменилось или выключилось: то, что было зажато по PTT,
        // ждёт отпускания, которого не будет.
        _resetTracking.Execute();

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            _midi.Close();
            _bindings.DeviceName = null;
            _logger.LogInformation("MIDI выключен");

            return new SelectMidiDeviceResult(MidiResult<MidiDeviceDto>.Ok(MidiDeviceDto.None));
        }

        // Порт умеет открывать только то, что он же и перечислил. Ищем здесь,
        // а не подставляем первое доступное: молча перенаправить привязки на
        // чужой контроллер опаснее, чем промолчать.
        var device = _midi.Devices.FirstOrDefault(d => d.Name == deviceName);
        if (device is null)
        {
            _logger.LogWarning(
                "MIDI-устройство «{Device}» не найдено среди {Count} подключённых",
                deviceName, _midi.Devices.Count);
            return new SelectMidiDeviceResult(MidiResult<MidiDeviceDto>.Fail(MidiError.DeviceNotFound));
        }

        _bindings.DeviceName = deviceName;
        _midi.Open(deviceName);

        if (!_midi.IsOpen)
        {
            _logger.LogWarning(
                "MIDI-устройство «{Device}» есть в списке, но не открылось — занято другим приложением?",
                deviceName);
            return new SelectMidiDeviceResult(MidiResult<MidiDeviceDto>.Fail(MidiError.DeviceOpenFailed));
        }

        _logger.LogInformation("MIDI открыт: «{Device}»", deviceName);
        return new SelectMidiDeviceResult(MidiResult<MidiDeviceDto>.Ok(new MidiDeviceDto(device)));
    }

    /// <summary>
    /// Пункты выпадающего списка устройств для окна: «выключено» плюс всё, что
    /// порт видит сейчас.
    /// </summary>
    public IReadOnlyList<MidiDeviceDto> GetAvailableDevices() => MidiDeviceDto.BuildFor(_midi.Devices);

    /// <summary>
    /// Какой пункт показать выбранным, если в настройках сохранено устройство.
    /// Правило живёт в <see cref="MidiDeviceDto.Resolve"/>: отключённый
    /// контроллер показывается как «выключено», но имя из настроек не стирается.
    /// </summary>
    public MidiDeviceDto ResolveSavedDevice(IReadOnlyList<MidiDeviceDto> options) =>
        MidiDeviceDto.Resolve(options, _bindings.DeviceName);
}