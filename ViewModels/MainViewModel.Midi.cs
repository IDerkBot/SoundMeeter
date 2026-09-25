using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

// Применение MIDI-привязок: непрерывные параметры, кнопки и PTT.
public partial class MainViewModel
{
    private readonly IMidiService _midi;
    private readonly Dictionary<string, bool> _midiPressed = new();
    private readonly Dictionary<string, bool> _pttRestore = new();

    /// <summary>Доступ к MIDI-сервису (для окна привязок).</summary>
    public IMidiService MidiService => _midi;

    /// <summary>Уведомляет VM об изменении MIDI-настроек (из окна привязок).</summary>
    public void MidiSettingsChanged() => MarkDirty();

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
                case MidiParamShape.Button when binding.IsMomentary:
                    ApplyMidiMomentary(binding, msg);
                    break;
                case MidiParamShape.Button:
                    ApplyMidiButton(binding, descriptor, msg);
                    break;
                default:
                    ApplyMidiContinuous(binding, descriptor, msg);
                    break;
            }
        }
    }

    /// <summary>
    /// Сопоставляет сообщение с привязкой. Для PTT на NoteOn дополнительно
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

    private void ApplyMidiButton(MidiBinding binding, MidiParameterDescriptor descriptor, MidiMessageInfo msg)
    {
        // Кнопка: переключаем по фронту нажатия (value>=64, velocity>0).
        // Отпускание (NoteOff / CC<64) лишь помечает кнопку отпущенной.
        bool pressed = msg.Kind switch
        {
            MidiMessageKind.NoteOn => msg.Value > 0,
            MidiMessageKind.NoteOff => false,
            _ => msg.Value >= 64
        };

        string key = $"{binding.TargetType}|{binding.StripId}|{binding.Parameter}";
        bool wasPressed = _midiPressed.TryGetValue(key, out var prev) && prev;
        _midiPressed[key] = pressed;
        if (pressed == wasPressed) return;
        if (!pressed) return; // только фронт нажатия

        switch (binding.TargetType)
        {
            case "Input":
                var input = Inputs.FirstOrDefault(i => i.Id == binding.StripId);
                if (input == null) return;
                ToggleButton(input, binding.Parameter);
                break;
            case "Bus":
                var bus = Buses.FirstOrDefault(b => b.Id == binding.StripId);
                if (bus == null) return;
                ToggleButton(bus, binding.Parameter);
                break;
        }
    }

    /// <summary>
    /// Momentary-привязка (PTT): пока кнопка нажата — параметр активен
    /// (Mute — микрофон открыт, Solo/Mono/Denoiser — включены), на отпускание
    /// возвращается прежнее состояние.
    /// </summary>
    private void ApplyMidiMomentary(MidiBinding binding, MidiMessageInfo msg)
    {
        bool pressed = msg.Kind switch
        {
            MidiMessageKind.NoteOn => msg.Value > 0,
            MidiMessageKind.NoteOff => false,
            _ => msg.Value >= 64
        };

        string key = $"{binding.TargetType}|{binding.StripId}|{binding.Parameter}";
        bool wasPressed = _midiPressed.TryGetValue(key, out var prev) && prev;
        _midiPressed[key] = pressed;
        if (pressed == wasPressed) return;

        if (binding.TargetType != "Input") return;

        var input = Inputs.FirstOrDefault(i => i.Id == binding.StripId);
        if (input == null) return;

        string parameter = binding.Parameter;
        if (!IsMomentaryParam(parameter)) return;

        if (pressed)
        {
            // Запомнили состояние до нажатия и включили параметр.
            _pttRestore[key] = GetButtonState(input, parameter);
            SetButtonState(input, parameter, MomentaryActiveValue(parameter));
        }
        else if (_pttRestore.TryGetValue(key, out var restore))
        {
            // Отпустили — вернули прежнее состояние.
            SetButtonState(input, parameter, restore);
            _pttRestore.Remove(key);
        }
    }

    private static bool IsMomentaryParam(string parameter) => parameter switch
    {
        "IsMuted" or "IsSolo" or "IsMono" or "DenoiserEnabled" => true,
        _ => false
    };

    /// <summary>Значение параметра, пока кнопка PTT нажата (Mute — открыть звук).</summary>
    private static bool MomentaryActiveValue(string parameter) =>
        parameter == "IsMuted" ? false : true;

    private static bool GetButtonState(InputChannelViewModel vm, string parameter) => parameter switch
    {
        "IsMuted" => vm.IsMuted,
        "IsSolo" => vm.IsSolo,
        "IsMono" => vm.IsMono,
        "DenoiserEnabled" => vm.DenoiserEnabled,
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

    private static void SetContinuous(InputChannelViewModel vm, string parameter, float value)
    {
        switch (parameter)
        {
            case "VolumeDb": vm.VolumeDb = value; break;
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
