using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

// Применение MIDI-привязок: непрерывные параметры и кнопки (переключение,
// удержание-PTT, кнопка с фиксацией).
public partial class MainViewModel
{
    private readonly IMidiService _midi;
    private readonly Dictionary<string, bool> _midiPressed = new();
    private readonly Dictionary<string, bool> _pttRestore = new();

    /// <summary>Доступ к MIDI-сервису (для окна привязок).</summary>
    public IMidiService MidiService => _midi;

    /// <summary>Уведомляет VM об изменении MIDI-настроек (из окна привязок).</summary>
    public void MidiSettingsChanged()
    {
        ResetMidiButtonStates();
        _logger.LogInformation("MIDI-настройки изменены: устройство «{Device}», привязок {Count}",
            _engine.Midi.DeviceName ?? "<не выбрано>", _engine.Midi.Bindings.Count);
        MarkDirty();
    }

    /// <summary>
    /// Сбрасывает отслеживание нажатий: сменилось устройство, привязка или её
    /// режим — иначе первое же сообщение будет съедено как «уже нажато».
    /// </summary>
    public void ResetMidiButtonStates()
    {
        // PTT-кнопки, которые сейчас «зажаты», надо вернуть в прежнее состояние,
        // иначе параметр останется включённым навсегда.
        if (_pttRestore.Count > 0)
        {
            foreach (var pair in _pttRestore)
            {
                var binding = _engine.Midi.Bindings.FirstOrDefault(b => ButtonKey(b) == pair.Key);
                if (binding != null) SetButtonState(binding, pair.Value);
            }
            _pttRestore.Clear();
        }
        _midiPressed.Clear();
    }

    /// <summary>MIDI-сообщение пришло — применяем к привязкам (на UI-потоке).</summary>
    private void OnMidiMessageReceived(MidiMessageInfo msg)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) ApplyMidiMessage(msg);
        else dispatcher.BeginInvoke(() => ApplyMidiMessage(msg));
    }

    private void ApplyMidiMessage(MidiMessageInfo msg)
    {
        foreach (var binding in _engine.Midi.Bindings)
        {
            if (!MatchesKind(binding, msg)) continue;
            if (binding.Channel != msg.Channel) continue;
            if (binding.MessageKind != MidiMessageKind.PitchWheel && binding.Control != msg.Control) continue;

            var descriptor = MidiParameters.All.FirstOrDefault(d => d.Key == binding.Parameter);
            if (descriptor == null) continue;

            switch (descriptor.Shape)
            {
                case MidiParamShape.Button:
                    ApplyMidiButton(binding, msg);
                    break;
                default:
                    ApplyMidiContinuous(binding, descriptor, msg);
                    break;
            }
        }
    }

    /// <summary>
    /// Сопоставляет сообщение с привязкой. Для привязки к ноте дополнительно
    /// принимаем NoteOff — отпускание ноты приходит именно этим событием.
    /// </summary>
    private static bool MatchesKind(MidiBinding binding, MidiMessageInfo msg)
    {
        if (binding.MessageKind == msg.Kind) return true;
        return binding.MessageKind == MidiMessageKind.NoteOn && msg.Kind == MidiMessageKind.NoteOff;
    }

    private void ApplyMidiContinuous(MidiBinding binding, MidiParameterDescriptor descriptor, MidiMessageInfo msg)
    {
        float normalized = binding.MessageKind == MidiMessageKind.PitchWheel
            ? msg.Value / 16383f
            : msg.Value / 127f;
        float value = descriptor.Min + (descriptor.Max - descriptor.Min) * normalized;
        _logger.LogDebug("MIDI {Param} = {Value:F2} ({Target} {Strip})",
            binding.Parameter, value, binding.TargetType, binding.StripId);

        switch (binding.TargetType)
        {
            case "Input":
                var input = Inputs.FirstOrDefault(i => i.Id == binding.StripId);
                if (input == null) return;
                SetContinuous(input, binding.Parameter, value);
                break;
            case "Bus":
                var bus = Buses.FirstOrDefault(b => b.Id == binding.StripId);
                if (bus == null) return;
                SetContinuous(bus, binding.Parameter, value);
                break;
        }
    }

    private static string ButtonKey(MidiBinding binding) =>
        $"{binding.TargetType}|{binding.StripId}|{binding.Parameter}";

    /// <summary>
    /// Нажата ли кнопка. Для CC обычная полярность — 127 = нажато, но кнопки
    /// с фиксацией на части MIDI-микшеров шлют наоборот (0 = нажато, 127 = отпущено);
    /// такая полярность включается флагом IsInverted у привязки.
    /// </summary>
    private static bool IsPressed(MidiBinding binding, MidiMessageInfo msg) => msg.Kind switch
    {
        MidiMessageKind.NoteOn => msg.Value > 0,
        MidiMessageKind.NoteOff => false,
        _ => binding.IsInverted ? msg.Value < 64 : msg.Value >= 64
    };

    /// <summary>
    /// Кнопочная привязка. Режим определяет, что делать с фронтом нажатия:
    /// Toggle — переключить, Hold — включить и вернуть по отпусканию,
    /// Latch — поставить параметр ровно в присланное контроллером состояние.
    /// </summary>
    private void ApplyMidiButton(MidiBinding binding, MidiMessageInfo msg)
    {
        bool pressed = IsPressed(binding, msg);

        string key = ButtonKey(binding);
        bool wasPressed = _midiPressed.TryGetValue(key, out var prev) && prev;
        _midiPressed[key] = pressed;
        if (pressed == wasPressed) return;

        // Режим кнопки виден только здесь: по журналу понятно, почему параметр
        // переключился (Toggle) или удержался (Hold/Latch).
        _logger.LogInformation("MIDI {Param} [{Mode}] {State} ({Target} {Strip})",
            binding.Parameter, binding.Mode, pressed ? "нажато" : "отпущено",
            binding.TargetType, binding.StripId);

        switch (binding.Mode)
        {
            case MidiButtonMode.Hold:
                ApplyMidiHold(binding, key, pressed);
                break;
            case MidiButtonMode.Latch:
                ApplyMidiLatch(binding, pressed);
                break;
            default:
                if (!pressed) return; // Toggle реагирует только на фронт нажатия
                ToggleButton(binding);
                break;
        }
    }

    /// <summary>
    /// PTT по удержанию: пока кнопка нажата — параметр активен
    /// (Mute — микрофон открыт, Solo/Mono/Denoiser — включены), на отпускание
    /// возвращается прежнее состояние.
    /// </summary>
    private void ApplyMidiHold(MidiBinding binding, string key, bool pressed)
    {
        if (!IsButtonParam(binding.Parameter)) return;

        if (pressed)
        {
            // Запомнили состояние до нажатия и включили параметр.
            _pttRestore[key] = GetButtonState(binding);
            SetButtonState(binding, ButtonActiveValue(binding.Parameter));
        }
        else if (_pttRestore.Remove(key, out var restore))
        {
            // Отпустили — вернули прежнее состояние.
            SetButtonState(binding, restore);
        }
    }

    /// <summary>
    /// Кнопка с фиксацией: контроллер шлёт не нажатие, а состояние
    /// («включено»/«выключено»), поэтому параметр ставится ровно в это состояние.
    /// Одно нажатие фиксированной кнопки включает микрофон, следующее — выключает.
    /// </summary>
    private void ApplyMidiLatch(MidiBinding binding, bool pressed)
    {
        if (!IsButtonParam(binding.Parameter)) return;
        bool active = ButtonActiveValue(binding.Parameter);
        SetButtonState(binding, pressed == active);
    }

    private static bool IsButtonParam(string parameter) => parameter switch
    {
        "IsMuted" or "IsSolo" or "IsMono" or "DenoiserEnabled" => true,
        _ => false
    };

    /// <summary>Значение параметра, пока кнопка нажата (Mute — открыть звук).</summary>
    private static bool ButtonActiveValue(string parameter) =>
        parameter == "IsMuted" ? false : true;

    private void ToggleButton(MidiBinding binding)
    {
        switch (binding.TargetType)
        {
            case "Input":
                var input = Inputs.FirstOrDefault(i => i.Id == binding.StripId);
                if (input != null) ToggleButton(input, binding.Parameter);
                break;
            case "Bus":
                var bus = Buses.FirstOrDefault(b => b.Id == binding.StripId);
                if (bus != null) ToggleButton(bus, binding.Parameter);
                break;
        }
    }

    private bool GetButtonState(MidiBinding binding) => binding.TargetType switch
    {
        "Input" => Inputs.FirstOrDefault(i => i.Id == binding.StripId) is { } input
            ? GetButtonState(input, binding.Parameter)
            : false,
        "Bus" => Buses.FirstOrDefault(b => b.Id == binding.StripId) is { } bus
            ? GetButtonState(bus, binding.Parameter)
            : false,
        _ => false
    };

    private void SetButtonState(MidiBinding binding, bool value)
    {
        switch (binding.TargetType)
        {
            case "Input":
                var input = Inputs.FirstOrDefault(i => i.Id == binding.StripId);
                if (input != null) SetButtonState(input, binding.Parameter, value);
                break;
            case "Bus":
                var bus = Buses.FirstOrDefault(b => b.Id == binding.StripId);
                if (bus != null) SetButtonState(bus, binding.Parameter, value);
                break;
        }
    }

    private static bool GetButtonState(InputChannelViewModel vm, string parameter) => parameter switch
    {
        "IsMuted" => vm.IsMuted,
        "IsSolo" => vm.IsSolo,
        "IsMono" => vm.IsMono,
        "DenoiserEnabled" => vm.DenoiserEnabled,
        _ => false
    };

    private static bool GetButtonState(OutputBusViewModel vm, string parameter) => parameter switch
    {
        "IsMuted" => vm.IsMuted,
        "IsSolo" => vm.IsSolo,
        "IsMono" => vm.IsMono,
        _ => false
    };

    private static void SetButtonState(InputChannelViewModel vm, string parameter, bool value)
    {
        switch (parameter)
        {
            case "IsMuted": vm.IsMuted = value; break;
            case "IsSolo": vm.IsSolo = value; break;
            case "IsMono": vm.IsMono = value; break;
            case "DenoiserEnabled": vm.DenoiserEnabled = value; break;
        }
    }

    private static void SetButtonState(OutputBusViewModel vm, string parameter, bool value)
    {
        switch (parameter)
        {
            case "IsMuted": vm.IsMuted = value; break;
            case "IsSolo": vm.IsSolo = value; break;
            case "IsMono": vm.IsMono = value; break;
        }
    }

    private static void SetContinuous(InputChannelViewModel vm, string parameter, float value)
    {
        switch (parameter)
        {
            case "VolumeDb": vm.VolumeDb = value; break;
            case "GainDb": vm.GainDb = value; break;
            case "DenoiserNoiseRemover": vm.DenoiserNoiseRemover = value; break;
            case "DenoiserDryWet": vm.DenoiserDryWet = value; break;
            case "DenoiserFormantLowDb": vm.DenoiserFormantLowDb = value; break;
            case "DenoiserFormantMidDb": vm.DenoiserFormantMidDb = value; break;
            case "DenoiserFormantHighDb": vm.DenoiserFormantHighDb = value; break;
            case "DenoiserFormantGroupDb": vm.DenoiserFormantGroupDb = value; break;
        }
    }

    private static void SetContinuous(OutputBusViewModel vm, string parameter, float value)
    {
        if (parameter == "VolumeDb") vm.VolumeDb = value;
    }

    private static void ToggleButton(InputChannelViewModel vm, string parameter)
    {
        switch (parameter)
        {
            case "IsMuted": vm.IsMuted = !vm.IsMuted; break;
            case "IsMono": vm.IsMono = !vm.IsMono; break;
            case "IsSolo": vm.IsSolo = !vm.IsSolo; break;
            case "DenoiserEnabled": vm.DenoiserEnabled = !vm.DenoiserEnabled; break;
        }
    }

    private static void ToggleButton(OutputBusViewModel vm, string parameter)
    {
        switch (parameter)
        {
            case "IsMuted": vm.IsMuted = !vm.IsMuted; break;
            case "IsMono": vm.IsMono = !vm.IsMono; break;
            case "IsSolo": vm.IsSolo = !vm.IsSolo; break;
        }
    }
}
