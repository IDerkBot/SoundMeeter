using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;
using SoundMeeter.Midi.Entities;
using SoundMeeter.Midi.Presentation.Dto;
using SoundMeeter.Midi.Presentation.Helpers;

namespace SoundMeeter.Midi.Presentation.ViewModels;

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

    private static string FormatBinding(MidiBindingDto b) => b.MessageKind switch
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
