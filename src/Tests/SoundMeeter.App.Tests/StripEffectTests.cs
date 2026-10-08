using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views.Controls;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Эффекты стрипа (SM-B05): четыре эффекта, их крутилки, включение в модель и
/// попап с параметрами. Крутилка проверяется и во ViewModel, и в разметке:
/// привязка float (модель) → double (свойство элемента) может не сработать
/// молча, и попап покажет нули вместо сохранённых значений.
/// </summary>
public class StripEffectTests
{
    [Fact]
    public void StripHasFourEffects()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.NotNull(vm.Compressor);
        Assert.NotNull(vm.FxGain);
        Assert.NotNull(vm.Delay);
        Assert.NotNull(vm.Reverb);
    }

    [Fact]
    public void EachEffectHasItsOwnKnobSet()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.Equal(5, vm.Compressor.Knobs.Count);   // порог, отношение, атака, спад, компенсация
        Assert.Single(vm.FxGain.Knobs);               // trim
        Assert.Equal(4, vm.Delay.Knobs.Count);        // время, обратная связь, демпфирование, микс
        Assert.Equal(3, vm.Reverb.Knobs.Count);       // размер, демпфирование, микс
    }

    [Fact]
    public void KnobNamesAndUnitsComeFromResources()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.All(vm.Compressor.Knobs, k =>
        {
            Assert.NotEmpty(k.Name);
            Assert.DoesNotContain("Sm.", k.Name);
        });

        Assert.Contains("ms", vm.Delay.Knobs[0].Display);
        Assert.Contains("%", vm.Delay.Knobs[3].Display);
    }

    [Fact]
    public void KnobWritesThroughToTheModelAndClampsToItsRange()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        var time = vm.Delay.Knobs[0];

        time.Value = 500f;
        Assert.Equal(500f, vm.Model.DelayTimeMs);

        // Крутилка не должна дать записать в пресет значение, которое DSP потом
        // срежет: границы VM и обработки обязаны совпадать.
        time.Value = 99999f;
        Assert.Equal(2000f, vm.Model.DelayTimeMs);
    }

    [Fact]
    public void EffectToggleWritesTheModel()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        vm.Compressor.IsEnabled = true;
        Assert.True(vm.CompressorEnabled);
        Assert.True(vm.Model.CompressorEnabled);

        vm.Delay.IsEnabled = true;
        Assert.True(vm.DelayEnabled);
        Assert.True(vm.Model.DelayEnabled);
    }

    [Fact]
    public void EffectPopupRealizesAKnobPerParameterWithModelValues()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var popup = new StripEffectSettingsView { DataContext = vm.Compressor };
            VisualTree.Layout(popup, 272, 400);

            var knobs = VisualTree.FindAll<SoundMeeter.Controls.GainKnob>(popup);
            Assert.Equal(5, knobs.Count);
            Assert.Equal(vm.Model.CompressorThresholdDb, (float)knobs[0].Value, 2);
            Assert.Equal(vm.Model.CompressorAttackMs, (float)knobs[2].Value, 2);
            Assert.Equal(0f, (float)knobs[0].Minimum + 60f, 2);
            Assert.Equal(0f, (float)knobs[0].Maximum, 2);

            // Обратная привязка: крутка в попапе пишет в модель стрипа.
            knobs[0].Value = -30;
            Assert.Equal(-30f, vm.Model.CompressorThresholdDb, 2);
        });
    }

    [Fact]
    public void EffectButtonsSitBelowTheRoutingButtons()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var strip = new InputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);

            var compressor = VisualTree.FindToggle(strip, "CompressorBtn");
            var delay = VisualTree.FindToggle(strip, "DelayBtn");
            var func2 = VisualTree.FindToggle(strip, "Func2Btn");
            Assert.NotNull(compressor);
            Assert.NotNull(delay);
            Assert.NotNull(func2);

            // Порядок в колонке — часть интерфейса: сетка эффектов не должна
            // наезжать на кнопки роутинга, иначе их нельзя нажать.
            double func2Y = VisualTree.Y(func2!, strip)!.Value;
            double delayY = VisualTree.Y(delay!, strip)!.Value;
            Assert.True(delayY > func2Y, $"F2={func2Y:F0} DLY={delayY:F0}");
            Assert.InRange(strip.ActualHeight, 1, 900);
        });
    }

    [Fact]
    public void EffectTogglesShowWhatThePresetSays()
    {
        // DSP читает модель, а не ViewModel. Если бы кнопка брала состояние из
        // поля ViewModel по умолчанию, после загрузки settings.json все включённые
        // эффекты выглядели бы выключенными, хотя обработка уже работает.
        var model = new InputChannelModel
        {
            CompressorEnabled = true,
            FxGainEnabled = true,
            DelayEnabled = true,
            ReverbEnabled = true,
            EqEnabled = true
        };
        var vm = new InputChannelViewModel(
            model,
            new FakeAudioEngine(),
            Strips.Buses(),
            new List<SoundMeeter.Services.DeviceInfo>(),
            () => { });

        Assert.True(vm.CompressorEnabled);
        Assert.True(vm.FxGainEnabled);
        Assert.True(vm.DelayEnabled);
        Assert.True(vm.ReverbEnabled);
        Assert.True(vm.EqEnabled);
        Assert.True(vm.Equalizer.IsEnabled);
    }

    [Fact]
    public void ClickingAnEffectButtonTogglesTheEffect()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var strip = new InputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);
            var compressor = VisualTree.FindToggle(strip, "CompressorBtn")!;
            var delay = VisualTree.FindToggle(strip, "DelayBtn")!;

            vm.Delay.IsEnabled = true;
            strip.UpdateLayout();
            Assert.True(delay.IsChecked);

            VisualTree.Click(delay);
            Assert.False(vm.DelayEnabled);
            Assert.False(vm.Model.DelayEnabled);
            Assert.False(compressor.IsChecked);
        });
    }
}
