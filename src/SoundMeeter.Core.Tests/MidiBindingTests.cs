using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// MIDI-привязки кнопок FUNC и переключателей эффектов. Диспетчер MIDI живёт в
/// приватных методах <see cref="MainViewModel"/> и написан как чистые функции
/// над ViewModel — намеренно, чтобы их можно было проверить без MIDI-устройства:
/// иначе единственным способом поймать ошибку в привязке FUNC был бы физический
/// контроллер.
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
            MidiParameters.ReverbEnabledKey
        ];

        foreach (string key in keys)
        {
            var descriptor = MidiParameters.ForInput().Single(d => d.Key == key);
            Assert.Equal(MidiParamShape.Button, descriptor.Shape);
        }
    }

    [Fact]
    public void MidiPressAndReleaseDrivesTheFuncButton()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;

        Assert.True(IsButtonParam(MidiParameters.Func1Key));
        Assert.False(GetButtonState(vm, MidiParameters.Func1Key));
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));

        SetButtonState(vm, MidiParameters.Func1Key, true);
        Assert.True(vm.Func1.IsEngaged);
        Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(vm));
        Assert.True(GetButtonState(vm, MidiParameters.Func1Key));

        SetButtonState(vm, MidiParameters.Func1Key, false);
        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));
    }

    [Fact]
    public void MidiToggleFlipsTheFuncButton()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;

        ToggleButton(vm, MidiParameters.Func2Key);
        Assert.True(vm.Func2.IsEngaged);
        Assert.Equal("b1=1 b2=1 v1=0", Strips.Routing(vm));

        ToggleButton(vm, MidiParameters.Func2Key);
        Assert.False(vm.Func2.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));
    }

    [Fact]
    public void MidiOnAButtonWithoutAnAssignmentDoesNothing()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        vm.Func1.ClearCommand.Execute(null);
        vm.HardwareOutputs[0].IsEnabled = true;

        ToggleButton(vm, MidiParameters.Func1Key);

        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));
    }

    private const BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    private static bool IsButtonParam(string parameter) =>
        Method(nameof(IsButtonParam), typeof(string)).Invoke(null, [parameter]) is true;

    private static bool GetButtonState(InputChannelViewModel vm, string parameter) =>
        Method(nameof(GetButtonState), typeof(InputChannelViewModel), typeof(string))
            .Invoke(null, [vm, parameter]) is true;

    private static void SetButtonState(InputChannelViewModel vm, string parameter, bool value) =>
        Method(nameof(SetButtonState), typeof(InputChannelViewModel), typeof(string), typeof(bool))
            .Invoke(null, [vm, parameter, value]);

    private static void ToggleButton(InputChannelViewModel vm, string parameter) =>
        Method(nameof(ToggleButton), typeof(InputChannelViewModel), typeof(string))
            .Invoke(null, [vm, parameter]);

    private static MethodInfo Method(string name, params Type[] parameters) =>
        typeof(MainViewModel).GetMethod(name, PrivateStatic, null, parameters, null)
        ?? throw new InvalidOperationException(
            $"MainViewModel.{name}({string.Join(", ", parameters.Select(p => p.Name))}) не найден");
}
