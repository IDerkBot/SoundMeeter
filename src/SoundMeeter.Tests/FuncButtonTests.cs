using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Кнопки FUNC: назначение выходов, переключение, возврат базового роутинга
/// (SM-C08). Логика живёт в <see cref="InputChannelViewModel"/>, тесты гоняют
/// её без UI — кроме случаев, где важна сама кнопка.
/// </summary>
public class FuncButtonTests
{
    [Fact]
    public void PopupSplitsOutputsIntoOutAndVirt()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.Equal(2, vm.HardwareOutputs.Count);
        Assert.Single(vm.VirtualOutputs);
        Assert.Equal(2, vm.Func1.HardwareTargets.Count);
        Assert.Single(vm.Func1.VirtualTargets);
    }

    [Fact]
    public void DefaultLabelIsTheSlotNumber()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.Equal("F1", vm.Func1.ButtonText);
        Assert.Equal("F2", vm.Func2.ButtonText);
    }

    [Fact]
    public void NothingIsEngagedOnAFreshStrip()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.False(vm.Func1.IsEngaged);
        Assert.False(vm.Func2.IsEngaged);
        Assert.Equal(InputChannelModel.NoFuncEngaged, vm.Model.EngagedFunc);
    }

    [Fact]
    public void ButtonWithoutAssignmentCannotBeTurnedOn()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.False(vm.Func1.CanApply);
        Strips.Engage(vm, 0);

        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal("b1=0 b2=0 v1=0", Strips.Routing(vm));
    }

    [Fact]
    public void AssignmentIsStoredInTheModelAndLeavesRoutingAlone()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        vm.Func1.VirtualTargets[0].IsSelected = true;
        vm.Func2.HardwareTargets[0].IsSelected = true;
        vm.Func2.HardwareTargets[1].IsSelected = true;

        Assert.Equal(new[] { "v1" }, vm.Model.FuncButtons[0].BusIds);
        Assert.Equal(new[] { "b1", "b2" }, vm.Model.FuncButtons[1].BusIds);
        Assert.Equal("b1=0 b2=0 v1=0", Strips.Routing(vm));
        Assert.False(vm.Func1.IsEngaged);
    }

    [Fact]
    public void PressingAppliesTheRuleAndCapturesTheBase()
    {
        var engine = new FakeAudioEngine();
        var vm = Strips.Microphone(engine);
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));

        Strips.Engage(vm, 0);

        Assert.True(vm.Func1.IsEngaged);
        Assert.False(vm.Func2.IsEngaged);
        Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(vm));
        Assert.Contains(("v1", true), engine.Routes);
        Assert.Contains(("b1", false), engine.Routes);
        Assert.Equal(3, vm.Model.FuncBaseRouting.Count);
        Assert.True(vm.Model.FuncBaseRouting["b1"]);
    }

    [Fact]
    public void ReleasingRestoresTheBase()
    {
        var engine = new FakeAudioEngine();
        var vm = Strips.Microphone(engine);
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;

        Strips.Engage(vm, 0);
        Strips.Disengage(vm, 0);

        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal("b1=1 b2=0 v1=0", Strips.Routing(vm));
        Assert.Contains(("b1", true), engine.Routes);
    }

    [Fact]
    public void OnlyOneButtonCanBeEngagedAndSwitchingGoesThroughTheBase()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;

        Strips.Engage(vm, 1);
        Assert.True(vm.Func2.IsEngaged);
        Assert.Equal("b1=1 b2=1 v1=0", Strips.Routing(vm));

        Strips.Engage(vm, 0);
        Assert.True(vm.Func1.IsEngaged);
        Assert.False(vm.Func2.IsEngaged);
        Assert.Equal(0, vm.Model.EngagedFunc);

        // Правило второго слота не наслаивается на первое: каждое нажатие
        // возвращается к базе, иначе «в колонки» после «в кабель» оставил бы
        // стрип в обоих выходах.
        Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(vm));
    }

    [Fact]
    public void ManualEditDisengagesAndBecomesTheNewBase()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        vm.HardwareOutputs[0].IsEnabled = true;
        Strips.Engage(vm, 0);

        vm.HardwareOutputs[1].IsEnabled = true;

        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal(InputChannelModel.NoFuncEngaged, vm.Model.EngagedFunc);
        Assert.Equal("b1=0 b2=1 v1=1", Strips.Routing(vm));
        Assert.True(vm.Model.FuncBaseRouting["b2"]);
        Assert.True(vm.Model.FuncBaseRouting["v1"]);
    }

    [Fact]
    public void EditingAnAssignmentDisengagesButKeepsRouting()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        Strips.Engage(vm, 1);
        string before = Strips.Routing(vm);

        vm.Func2.HardwareTargets[0].IsSelected = false;

        Assert.False(vm.Func2.IsEngaged);
        Assert.Equal(before, Strips.Routing(vm));
    }

    [Fact]
    public void RelabelingKeepsTheButtonEngaged()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        Strips.Engage(vm, 0);

        vm.Func1.Label = "СТРИМ";

        Assert.True(vm.Func1.IsEngaged);
        Assert.Equal("СТРИМ", vm.Func1.ButtonText);
    }

    [Fact]
    public void LabelIsClampedToTheButtonWidth()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        vm.Func1.Label = "0123456789012345";

        Assert.Equal(12, vm.Func1.Label.Length);
    }

    [Fact]
    public void TurnOnAndTurnOffCommandsMatchTheToggle()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);

        vm.Func1.TurnOnCommand.Execute(null);
        Assert.True(vm.Func1.IsEngaged);

        vm.Func1.TurnOffCommand.Execute(null);
        Assert.False(vm.Func1.IsEngaged);
    }

    [Fact]
    public void ClearingAnAssignmentDisengagesAndKeepsRouting()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);
        Strips.Engage(vm, 0);
        string before = Strips.Routing(vm);

        vm.Func1.ClearCommand.Execute(null);

        Assert.False(vm.Func1.IsEngaged);
        Assert.Equal(before, Strips.Routing(vm));

        Strips.Engage(vm, 0);
        Assert.False(vm.Func1.CanApply);
        Assert.False(vm.Func1.IsEngaged);
    }

    [Fact]
    public void AdditiveButtonAppliesToTheBaseNotToTheOtherRule()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        Strips.AssignDefaults(vm);   // FUNC1 -> кабель, FUNC2 -> оба OUT
        vm.Func2.IsExclusive = false; // «дописать», а не «только свои»
        vm.HardwareOutputs[1].IsEnabled = true;  // база: стрип уже идёт в b2
        Assert.Equal("b1=0 b2=1 v1=0", Strips.Routing(vm));

        Strips.Engage(vm, 0);   // FUNC1 «только свои»: уходит в кабель, гасит b2
        Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(vm));

        Strips.Engage(vm, 1);   // FUNC2 «дописать»: возвращает базу и добавляет свои

        Assert.Equal("b1=1 b2=1 v1=0", Strips.Routing(vm));
    }

    [Fact]
    public void AButtonWithoutAssignmentStaysClickableSoItCanBeAssigned()
    {
        // Всё внутри одного UiHost.Run: элемент WPF нельзя трогать извне потока,
        // который им владеет, — вернуть его наружу и asserts уже не выполнить.
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var view = new InputStripView { DataContext = vm };
            VisualTree.Layout(view, 200, 600);
            var button = VisualTree.FindToggle(view, "Func1Btn")!;

            Assert.True(button.IsEnabled);

            VisualTree.Click(button);
            Assert.False(vm.Func1.IsEngaged);
            Assert.Equal("b1=0 b2=0 v1=0", Strips.Routing(vm));

            // Нажатие кнопки без назначения обязано открывать попап: назначать
            // выходы больше негде, попап открывается только по самой кнопке.
            var popupOf = typeof(InputStripView).GetMethod(
                "FuncPopupOf",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var popupField = typeof(InputStripView).GetField(
                "Func1Popup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.NotNull(popupOf);
            Assert.NotNull(popupField);
            Assert.Same(popupField!.GetValue(view), popupOf!.Invoke(view, new object?[] { button }));
        });
    }

    [Fact]
    public void ClickingWithAnAssignmentTogglesTheButton()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var view = new InputStripView { DataContext = vm };
            VisualTree.Layout(view, 200, 600);
            var button = VisualTree.FindToggle(view, "Func1Btn")!;

            vm.Func1.VirtualTargets[0].IsSelected = true;
            view.UpdateLayout();

            VisualTree.Click(button);
            Assert.True(vm.Func1.IsEngaged);
            Assert.True(button.IsChecked);
            Assert.Equal("b1=0 b2=0 v1=1", Strips.Routing(vm));

            VisualTree.Click(button);
            Assert.False(vm.Func1.IsEngaged);
            Assert.Equal("b1=0 b2=0 v1=0", Strips.Routing(vm));
        });
    }

    [Fact]
    public void ToggleFollowsTheViewModel()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            Strips.AssignDefaults(vm);
            var view = new InputStripView { DataContext = vm };
            VisualTree.Layout(view, 200, 600);
            var func1 = VisualTree.FindToggle(view, "Func1Btn")!;
            var func2 = VisualTree.FindToggle(view, "Func2Btn")!;

            Strips.Engage(vm, 1);
            view.UpdateLayout();
            Assert.False(func1.IsChecked);
            Assert.True(func2.IsChecked);

            Strips.Disengage(vm, 1);
            view.UpdateLayout();
            Assert.False(func2.IsChecked);
        });
    }

    [Fact]
    public void FuncStyleIsAToggleButton()
    {
        var theme = VisualTree.MixerTheme();

        Assert.True(theme.Contains("FuncBtn"));
        Assert.True(theme.Contains("FuncTargetTemplate"));
        var style = Assert.IsType<System.Windows.Style>(theme["FuncBtn"]);
        Assert.Equal(typeof(ToggleButton), style.TargetType);
    }

    [Fact]
    public void AssignmentPopupRealizesOneRowPerOutput()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var popup = new FuncButtonSettingsView { DataContext = vm.Func1 };
            VisualTree.Layout(popup, 272, 600);

            // Считаем именно строки выходов: в попапе есть ещё отдельный
            // флажок режима («только свои» / «дописать»), и он к назначению
            // не относится.
            var rows = VisualTree.FindAll<System.Windows.Controls.CheckBox>(popup)
                .Where(c => c.DataContext is FuncTargetViewModel)
                .ToList();

            Assert.Equal(3, rows.Count);
            Assert.Equal(
                new[] { Strips.SpeakerBusId, Strips.HeadphonesBusId, Strips.CableBusId },
                rows.Select(r => ((FuncTargetViewModel)r.DataContext).BusId));
        });
    }
}
