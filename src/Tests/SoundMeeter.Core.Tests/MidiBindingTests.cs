using SoundMeeter.Core.Contract;
using SoundMeeter.Core.Entities;
using SoundMeeter.Core.Presentation.ViewModels;
using SoundMeeter.Core.UseCases;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;
using SoundMeeter.Midi.UseCases;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// MIDI-привязки: каталог, доступ к стрипам и сценарии модуля.
///
/// Раньше эти проверки доставали приватные статические методы
/// <c>MidiViewModel</c> рефлексией по имени. Теперь та же логика живёт в
/// <see cref="MidiStripAccessor"/> и сценариях модуля, и вызывается напрямую:
/// переименование метода больше не выдаёт «метод не найден» вместо внятной
/// ошибки, а проверки не зависят от того, приватный метод или нет.
///
/// Контроллера по-прежнему нет — и это главное: единственным способом поймать
/// ошибку в привязке FUNC был бы физический MIDI-микшер. Сообщения порта
/// воспроизводятся здесь напрямую (<see cref="MidiMessageInfo"/>), а стрипы —
/// настоящие <c>InputChannelViewModel</c> над заглушкой движка.
/// </summary>
public class MidiBindingTests
{
    [Fact]
    public void FuncKeysAreOfferedForInputsOnly()
    {
        var func1 = MidiParameters.ForInput().Single(d => d.Key == MidiParameters.Func1Key);
        var func2 = MidiParameters.ForInput().Single(d => d.Key == MidiParameters.Func2Key);

        Assert.Equal(MidiParamShape.Button, func1.Shape);
        Assert.True(func1.ForInput);
        Assert.Equal(MidiParamShape.Button, func2.Shape);
        Assert.True(func2.ForInput);

        // На выходной шине у кнопок FUNC нет смысла: правило описывает, куда
        // уходит вход, а не что делает шина.
        Assert.DoesNotContain(MidiParameters.ForBus(), d =>
            d.Key is MidiParameters.Func1Key or MidiParameters.Func2Key);
    }

    [Fact]
    public void FuncKeysHaveTranslatedLabels()
    {
        var func1 = MidiParameters.ForInput().Single(d => d.Key == MidiParameters.Func1Key);
        var func2 = MidiParameters.ForInput().Single(d => d.Key == MidiParameters.Func2Key);

        Assert.Equal("Func 1", func1.Label);
        Assert.Equal("Func 2", func2.Label);
    }

    [Fact]
    public void EffectTogglesAreBindableForInputs()
    {
        string[] keys =
        [
            MidiParameters.CompressorEnabledKey,
            MidiParameters.FxGainEnabledKey,
            MidiParameters.DelayEnabledKey,
            MidiParameters.ReverbEnabledKey,
            MidiParameters.EqEnabledKey
        ];

        foreach (string key in keys)
        {
            var descriptor = MidiParameters.ForInput().Single(d => d.Key == key);
            Assert.Equal(MidiParamShape.Button, descriptor.Shape);
        }
    }

    // ── MidiStripAccessor: значения параметров ────────────────────────────────

    [Fact]
    public void SetButtonDrivesTheFuncButtonThroughTheStrip()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(strip);
        strip.HardwareOutputs[0].IsEnabled = true;

        var accessor = new MidiStripAccessor(new SingleStripProvider(strip, null));

        Assert.True(accessor.SetButton(MidiTargetTypes.Input, strip.Id,
            MidiParameters.Func1Key, true).IsSuccess);
        Assert.True(strip.Func1.IsEngaged);
        Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(strip));

        Assert.True(accessor.SetButton(MidiTargetTypes.Input, strip.Id,
            MidiParameters.Func1Key, false).IsSuccess);
        Assert.False(strip.Func1.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(strip));
    }

    [Fact]
    public void ToggleFlipsTheFuncButton()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(strip);
        strip.HardwareOutputs[0].IsEnabled = true;

        var accessor = new MidiStripAccessor(new SingleStripProvider(strip, null));

        Assert.True(accessor.ToggleButton(MidiTargetTypes.Input, strip.Id, MidiParameters.Func2Key).IsSuccess);
        Assert.True(strip.Func2.IsEngaged);

        Assert.True(accessor.ToggleButton(MidiTargetTypes.Input, strip.Id, MidiParameters.Func2Key).IsSuccess);
        Assert.False(strip.Func2.IsEngaged);
    }

    [Fact]
    public void ButtonWithoutAnAssignmentDoesNothing()
    {
        // Никаких назначений: переключение не должно само назначать выход.
        var strip = Strips.Microphone(new FakeAudioEngine());
        strip.Func1.ClearCommand.Execute(null);
        strip.HardwareOutputs[0].IsEnabled = true;

        var accessor = new MidiStripAccessor(new SingleStripProvider(strip, null));

        Assert.True(accessor.ToggleButton(MidiTargetTypes.Input, strip.Id, MidiParameters.Func1Key).IsSuccess);
        Assert.False(strip.Func1.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(strip));
    }

    [Fact]
    public void ToggleDrivesTheEqualizerThroughTheStrip()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        var accessor = new MidiStripAccessor(new SingleStripProvider(strip, null));

        Assert.True(accessor.ToggleButton(MidiTargetTypes.Input, strip.Id,
            MidiParameters.EqEnabledKey).IsSuccess);

        // Включение идёт через стрип, а не напрямую в модель: только стрип
        // знает, что настройка стала правкой пресета.
        Assert.True(strip.EqEnabled);
        Assert.True(strip.Model.EqEnabled);

        Assert.True(accessor.SetButton(MidiTargetTypes.Input, strip.Id,
            MidiParameters.EqEnabledKey, false).IsSuccess);
        Assert.False(strip.Model.EqEnabled);
    }

    // ── MidiStripAccessor: ошибки, которые раньше проглатывались ───────────────

    [Fact]
    public void MissingStripIsReportedInsteadOfIgnored()
    {
        // Раньше привязка на удалённую полосу просто игнорировалась, и у
        // пользователя было «кнопка не реагирует» без единого слова в журнале.
        var accessor = new MidiStripAccessor(new SingleStripProvider(null, null));

        var result = accessor.SetButton(MidiTargetTypes.Input, "i-died", MidiParameters.Func1Key, true);

        Assert.False(result.IsSuccess);
        Assert.Equal(MidiError.StripMissing, result.Error);
        Assert.Equal("Sm.Midi.Error.StripMissing", result.MessageKey);
    }

    [Fact]
    public void ParameterTheStripDoesNotHaveIsReported()
    {
        // Кнопка эффекта на шине: у OutputBusViewModel нет CompressorEnabled.
        var bus = Strips.Buses()[0];
        var accessor = new MidiStripAccessor(new SingleStripProvider(null, bus));

        var result = accessor.SetButton(MidiTargetTypes.Bus, bus.Id,
            MidiParameters.CompressorEnabledKey, true);

        Assert.False(result.IsSuccess);
        Assert.Equal(MidiError.ParameterUnsupported, result.Error);
    }

    [Fact]
    public void UnknownTargetTypeIsRejected()
    {
        var accessor = new MidiStripAccessor(new SingleStripProvider(null, null));

        Assert.Equal(MidiError.ParameterUnsupported,
            accessor.SetButton("Input2", "i1", MidiParameters.Func1Key, true).Error);
    }

    // ── Нормализация значения ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, -60f)]
    [InlineData(127, 12f)]
    public void ControlChangeValueIsMappedOntoTheParameterRange(int value, float expected)
    {
        var descriptor = MidiParameters.All.Single(d => d.Key == "VolumeDb");

        float actual = MidiValueNormalizer.Normalize(descriptor,
            new MidiMessageInfo(MidiMessageKind.ControlChange, 0, 7, value));

        Assert.Equal(expected, actual, 3);
    }

    [Fact]
    public void TheMiddleOfTheScaleIsTheMiddleOfTheParameterRange()
    {
        // 64 из 127 — это чуть больше половины шкалы, и ровно −24 дБ ждать нельзя:
        // округление «до красивого» спрятало бы саму ошибку нормализации.
        var descriptor = MidiParameters.All.Single(d => d.Key == "VolumeDb");

        float actual = MidiValueNormalizer.Normalize(descriptor,
            new MidiMessageInfo(MidiMessageKind.ControlChange, 0, 7, 64));

        Assert.Equal(-23.7165f, actual, 4);
    }

    [Fact]
    public void PitchWheelUsesItsOwnFourteenBitScale()
    {
        var descriptor = MidiParameters.All.Single(d => d.Key == "VolumeDb");

        // Делить на 127 здесь нельзя: контроллер присылает 0…16383, и при
        // делении на 127 колесо работало бы только на первой восьмой шкалы.
        float top = MidiValueNormalizer.Normalize(descriptor,
            new MidiMessageInfo(MidiMessageKind.PitchWheel, 0, 0, MidiValueNormalizer.MaxPitchWheel));
        float middle = MidiValueNormalizer.Normalize(descriptor,
            new MidiMessageInfo(MidiMessageKind.PitchWheel, 0, 0, MidiValueNormalizer.MaxPitchWheel / 2));

        Assert.Equal(12f, top, 3);
        Assert.Equal(-24f, middle, 1);
    }

    [Fact]
    public void OutOfRangeValueDoesNotPushTheParameterOutOfItsScale()
    {
        var descriptor = MidiParameters.All.Single(d => d.Key == "VolumeDb");

        float actual = MidiValueNormalizer.Normalize(descriptor,
            new MidiMessageInfo(MidiMessageKind.ControlChange, 0, 7, 200));

        Assert.Equal(12f, actual, 3);
    }

    // ── Сценарии модуля ──────────────────────────────────────────────────────

    [Fact]
    public void BindingIsStoredOncePerParameterAndNotPerButton()
    {
        var engine = new FakeAudioEngine();
        var store = new MidiBindingStore(engine);
        var bind = new BindMidiControlUseCase(store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        Assert.True(bind.Execute(Command(MidiParameters.Func1Key, control: 10)).IsSuccess);
        Assert.True(bind.Execute(Command(MidiParameters.Func1Key, control: 20)).IsSuccess);

        // Переучивание второй кнопкой первой не должно оставить первую привязку
        // жить: иначе параметром управляли бы две кнопки сразу.
        Assert.Single(engine.Midi.Bindings);
        Assert.Equal(20, engine.Midi.Bindings[0].Control);
    }

    [Fact]
    public void BindingIsRejectedForAParameterTheTargetDoesNotHave()
    {
        var engine = new FakeAudioEngine();
        var store = new MidiBindingStore(engine);
        var bind = new BindMidiControlUseCase(store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        // У выходной шины нет кнопок FUNC.
        var result = bind.Execute(Command(MidiParameters.Func1Key, control: 10, targetType: MidiTargetTypes.Bus));

        Assert.False(result.IsSuccess);
        Assert.Equal(MidiError.ParameterUnsupported, result.Error);
        Assert.Empty(engine.Midi.Bindings);
    }

    [Fact]
    public void ClearingAnAbsentBindingIsNotAnError()
    {
        // Пользователь и так снял кнопку — повторное «очистить» должно быть
        // штатным, а не ошибкой в интерфейсе.
        var store = new MidiBindingStore(new FakeAudioEngine());
        var clear = new ClearMidiBindingUseCase(store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        Assert.True(clear.Execute(MidiTargetTypes.Input, "i1", MidiParameters.Func1Key).IsSuccess);
    }

    [Fact]
    public void ChangingModeOfAnAbsentBindingIsReported()
    {
        var store = new MidiBindingStore(new FakeAudioEngine());
        var update = new UpdateMidiBindingUseCase(store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        var result = update.SetMode(MidiTargetTypes.Input, "i1", MidiParameters.Func1Key, MidiButtonMode.Hold);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ControlChangeMessageTogglesTheBoundParameterOnlyOnTheFrontEdge()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        var engine = new FakeAudioEngine();
        var store = new MidiBindingStore(engine);
        var tracker = new MidiPressTracker();
        var route = new RouteMidiMessageUseCase(store,
            new MidiStripAccessor(new SingleStripProvider(strip, null)), tracker);

        store.Replace(Command(MidiParameters.EqEnabledKey, control: 7).ToBinding());

        // Контроллер шлёт значение каждые ~10 мс, пока кнопка удерживается.
        // Без проверки фронта Toggle щёлкал бы десять раз в секунду.
        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.True(strip.EqEnabled);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.True(strip.EqEnabled);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 0));
        Assert.True(strip.EqEnabled);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.False(strip.EqEnabled);
    }

    [Fact]
    public void HoldModeRestoresTheStateThatWasThereBeforeThePress()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        var store = new MidiBindingStore(new FakeAudioEngine());
        var tracker = new MidiPressTracker();
        var route = new RouteMidiMessageUseCase(store,
            new MidiStripAccessor(new SingleStripProvider(strip, null)), tracker);

        var command = Command(MidiParameters.CompressorEnabledKey, control: 7) with { Mode = MidiButtonMode.Hold };
        store.Replace(command.ToBinding());

        // До нажатия компрессор выключен; отпускание обязано вернуть именно это,
        // а не «выключено» по умолчанию.
        strip.CompressorEnabled = false;
        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.True(strip.CompressorEnabled);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 0));
        Assert.False(strip.CompressorEnabled);

        // И наоборот: если параметр уже был включён, он должен таким и остаться.
        strip.CompressorEnabled = true;
        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.True(strip.CompressorEnabled);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 0));
        Assert.True(strip.CompressorEnabled);
    }

    [Fact]
    public void ResettingReleasesAHeldPttButton()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        var store = new MidiBindingStore(new FakeAudioEngine());
        var tracker = new MidiPressTracker();
        var accessor = new MidiStripAccessor(new SingleStripProvider(strip, null));
        var reset = new ResetMidiTrackingUseCase(store, accessor, tracker);
        var route = new RouteMidiMessageUseCase(store, accessor, tracker);

        var command = Command(MidiParameters.CompressorEnabledKey, control: 7) with { Mode = MidiButtonMode.Hold };
        store.Replace(command.ToBinding());

        strip.CompressorEnabled = false;
        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.True(strip.CompressorEnabled);

        // Смена устройства или снятие привязки. Отпускания больше не придёт —
        // без отката компрессор остался бы включённым до перезапуска.
        Assert.True(reset.Execute().IsSuccess);
        Assert.False(strip.CompressorEnabled);
    }

    [Fact]
    public void MuteHoldOpensTheMicrophoneWhileHeld()
    {
        // У Mute активное состояние — «звук включён», то есть параметр
        // снимается. Если бы PTT ставил true, удержание глушило бы микрофон.
        var strip = Strips.Microphone(new FakeAudioEngine());
        var store = new MidiBindingStore(new FakeAudioEngine());
        var tracker = new MidiPressTracker();
        var route = new RouteMidiMessageUseCase(store,
            new MidiStripAccessor(new SingleStripProvider(strip, null)), tracker);

        var command = Command("IsMuted", control: 7) with { Mode = MidiButtonMode.Hold };
        store.Replace(command.ToBinding());

        strip.IsMuted = true;
        route.Execute(Message(MidiMessageKind.ControlChange, 7, 127));
        Assert.False(strip.IsMuted);

        route.Execute(Message(MidiMessageKind.ControlChange, 7, 0));
        Assert.True(strip.IsMuted);
    }

    [Fact]
    public void MessageForAnotherChannelIsIgnored()
    {
        var strip = Strips.Microphone(new FakeAudioEngine());
        var store = new MidiBindingStore(new FakeAudioEngine());
        var route = new RouteMidiMessageUseCase(store,
            new MidiStripAccessor(new SingleStripProvider(strip, null)), new MidiPressTracker());

        store.Replace(Command(MidiParameters.EqEnabledKey, control: 7, channel: 0).ToBinding());

        route.Execute(new MidiMessageInfo(MidiMessageKind.ControlChange, 5, 7, 127));

        Assert.False(strip.EqEnabled);
    }

    [Fact]
    public void NoteBindingAlsoReleasesOnNoteOff()
    {
        // Отпускание ноты приходит именно Note Off. Без этой асимметрии
        // удержание по ноте не отпускалось бы никогда.
        var strip = Strips.Microphone(new FakeAudioEngine());
        var store = new MidiBindingStore(new FakeAudioEngine());
        var route = new RouteMidiMessageUseCase(store,
            new MidiStripAccessor(new SingleStripProvider(strip, null)), new MidiPressTracker());

        var command = Command(MidiParameters.CompressorEnabledKey,
            control: 60, kind: MidiMessageKind.NoteOn) with { Mode = MidiButtonMode.Hold };
        store.Replace(command.ToBinding());

        route.Execute(Message(MidiMessageKind.NoteOn, 60, 127));
        Assert.True(strip.CompressorEnabled);

        route.Execute(Message(MidiMessageKind.NoteOff, 60, 0));
        Assert.False(strip.CompressorEnabled);
    }

    // ── Выбор устройства ─────────────────────────────────────────────────────

    [Fact]
    public void SelectingAnAbsentDeviceIsReportedAndNotSwappedForAnother()
    {
        var port = new FakeMidiService([new MidiInputDevice(0, "Launchpad")]);
        var store = new MidiBindingStore(new FakeAudioEngine());
        var select = new SelectMidiDeviceUseCase(port, store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        var result = select.Execute("Отсутствующий контроллер");

        // Подхватить первый попавшийся Launchpad было бы опаснее, чем промолчать.
        Assert.False(result.IsOpen);
        Assert.Equal(MidiError.DeviceNotFound, result.Selection.Error);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public void SelectingAStoredButUnpluggedDeviceKeepsItsName()
    {
        // Контроллер включают позже приложения. Потеряли бы имя — пришлось бы
        // выбирать заново после каждого переподключения.
        var port = new FakeMidiService([]);
        var engine = new FakeAudioEngine { Midi = new MidiSettings { DeviceName = "Launchpad" } };
        var store = new MidiBindingStore(engine);
        var select = new SelectMidiDeviceUseCase(port, store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        var options = select.GetAvailableDevices();
        var resolved = select.ResolveSavedDevice(options);

        Assert.True(resolved.IsNone);
        Assert.Equal("Launchpad", store.DeviceName);

        // Подключённых устройств нет, поэтому в списке остаётся один пункт —
        // «выключено». Если бы их было, «выключено» всё равно было бы первым.
        Assert.Single(options);
        Assert.True(options[0].IsNone);
    }

    [Fact]
    public void SelectingAConnectedDeviceOpensItAndSavesTheName()
    {
        var port = new FakeMidiService([new MidiInputDevice(0, "Launchpad")]);
        var store = new MidiBindingStore(new FakeAudioEngine());
        var select = new SelectMidiDeviceUseCase(port, store, new ResetMidiTrackingUseCase(
            store, new MidiStripAccessor(new SingleStripProvider(null, null)), new MidiPressTracker()));

        var result = select.Execute("Launchpad");

        Assert.True(result.IsOpen);
        Assert.True(port.IsOpen);
        Assert.Equal("Launchpad", store.DeviceName);
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    private static MidiBindingCommand Command(
        string parameter,
        int control,
        int channel = 0,
        string targetType = MidiTargetTypes.Input,
        MidiMessageKind kind = MidiMessageKind.ControlChange) =>
        new(targetType, "i1", parameter, channel, control, kind, MidiButtonMode.Toggle, false);

    private static MidiMessageInfo Message(MidiMessageKind kind, int control, int value) =>
        new(kind, 0, control, value);

    /// <summary>
    /// Поставщик стрипов с нулями нужного состава. Пустые списки — частый
    /// случай: большинство проверок смотрит в ошибку, а не в значение.
    /// </summary>
    private sealed class SingleStripProvider(IInputStrip? input, OutputBusModel? bus) : IStripProvider
    {
        public IReadOnlyList<IInputStrip> Inputs { get; } =
            input is null ? [] : [input];

        public IReadOnlyList<IOutputStrip> Buses { get; } =
            bus is null ? [] : [new OutputBusViewModel(new FakeAudioEngine(), bus, () => { })];
    }

    /// <summary>
    /// Порт без WinMМ и NAudio: список устройств задаётся тестом, а Open/Close
    /// просто переключают флаг. Проверяется ровно то, что проверяет сценарий —
    /// реакция на результат открытия, а не поведение драйвера.
    /// </summary>
    private sealed class FakeMidiService(IReadOnlyList<MidiInputDevice> devices) : IMidiService
    {
        public IReadOnlyList<MidiInputDevice> Devices { get; } = devices;

        public bool IsOpen { get; private set; }

        /// <summary>Устройство, на котором Open должен упасть (как если бы его держали).</summary>
        public bool FailToOpen { get; init; }

        public void Open(string? deviceName)
        {
            IsOpen = !FailToOpen && !string.IsNullOrWhiteSpace(deviceName);
        }

        public void Close() => IsOpen = false;

        public event Action<MidiMessageInfo>? MessageReceived;

        public void Dispose() => Close();

        public void Raise(MidiMessageInfo message) => MessageReceived?.Invoke(message);
    }
}