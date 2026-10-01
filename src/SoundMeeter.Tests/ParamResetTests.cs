using SoundMeeter.Controls;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views.Controls;
using System.Windows.Controls;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Сброс регулятора двойным щелчком к значению по умолчанию (SM-C10). Кнопка в
/// UI одна на все типы регуляторов (<c>ParamReset.Command</c>), но проверять
/// её надо на каждом: у фейдера и посылки дефолт 0 дБ, у крутилки задержки
/// 250 мс, и общий дефолт «ноль» сломал бы эффекты молча.
/// </summary>
public class ParamResetTests
{
    [Fact]
    public void DoubleClickOnTheStripFaderResetsToZero()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var strip = new InputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);
            var fader = VisualTree.FindByName<Slider>(strip, "FaderOf") ?? VisualTree.FindAll<Slider>(strip)[0];

            vm.VolumeDb = -12f;
            VisualTree.DoubleClick(fader);

            Assert.Equal(0f, vm.VolumeDb);
            Assert.Equal(0f, vm.Model.VolumeDb);

            // Регулятор должен и сам показать 0, а не остаться с −12: значение
            // в модели и на ползунке разошлись бы, и юзер бы это видел.
            Assert.Equal(0d, fader.Value, 3);
        });
    }

    [Fact]
    public void DoubleClickOnTheBusFaderResetsToZero()
    {
        var model = new OutputBusModel { Id = "b1", Name = "Speakers", DeviceId = "dev1", VolumeDb = -9f };
        var vm = new OutputBusViewModel(new FakeAudioEngine(), model, () => { });

        UiHost.Run(() =>
        {
            var strip = new OutputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);
            var fader = VisualTree.FindAll<Slider>(strip)[0];
            Assert.Equal(-9d, fader.Value, 2);

            VisualTree.DoubleClick(fader);

            Assert.Equal(0f, vm.VolumeDb);
            Assert.Equal(0f, model.VolumeDb);
        });
    }

    [Fact]
    public void DoubleClickOnTheGainKnobResetsToZero()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var strip = new InputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);
            var knob = VisualTree.FindAll<GainKnob>(strip)[0];

            vm.GainDb = 25f;
            VisualTree.DoubleClick(knob);

            Assert.Equal(0f, vm.GainDb);
            Assert.Equal(0f, vm.Model.GainDb);
        });
    }

    [Fact]
    public void DoubleClickOnTheSendSliderResetsToZero()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var option = vm.HardwareOutputs[0];
            option.IsEnabled = true;
            option.GainDb = -8f;

            var row = new VisualTree.ThemeTemplateHost("OutputOptionTemplate") { Content = option };
            VisualTree.Layout(row, 260, 60);
            var slider = VisualTree.FindAll<Slider>(row)[0];
            Assert.Equal(-8d, slider.Value, 2);

            VisualTree.DoubleClick(slider);

            Assert.Equal(0f, option.GainDb);
            Assert.Equal(0d, slider.Value, 3);
        });
    }

    [Fact]
    public void DoubleClickOnAnEffectKnobReturnsItsOwnDefault()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var popup = new StripEffectSettingsView { DataContext = vm.Delay };
            VisualTree.Layout(popup, 272, 400);
            var knobs = VisualTree.FindAll<GainKnob>(popup);
            Assert.Equal(4, knobs.Count);

            // Порог компрессора — не ноль, а −18 дБ: сброс «в ноль» сломал бы
            // и обработку, и сохранённый пресет.
            knobs[0].Value = 0;
            VisualTree.DoubleClick(knobs[0]);
            Assert.Equal(250f, vm.Model.DelayTimeMs, 3);

            var compressorPopup = new StripEffectSettingsView { DataContext = vm.Compressor };
            VisualTree.Layout(compressorPopup, 272, 400);
            var threshold = VisualTree.FindAll<GainKnob>(compressorPopup)[0];
            threshold.Value = -40;
            VisualTree.DoubleClick(threshold);
            Assert.Equal(-18f, vm.Model.CompressorThresholdDb, 2);
        });
    }

    [Fact]
    public void ResetCommandUsesTheKnobsOwnDefault()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        var time = vm.Delay.Knobs[0];
        var mix = vm.Delay.Knobs[3];

        time.Value = 1234f;
        time.ResetCommand.Execute(null);
        Assert.Equal(250f, vm.Model.DelayTimeMs, 3);

        mix.Value = 80f;
        mix.ResetCommand.Execute(null);
        Assert.Equal(0f, vm.Model.DelayMix, 3);
        Assert.Equal(250f, time.DefaultValue, 3);
    }

    [Fact]
    public void DenoiserSlidersResetToHundredPercentAndZeroDecibels()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        vm.DenoiserNoiseRemover = 40f;
        vm.DenoiserFormantMidDb = 6f;
        vm.ResetDenoiserNoiseRemoverCommand.Execute(null);
        vm.ResetDenoiserFormantMidDbCommand.Execute(null);

        Assert.Equal(100f, vm.DenoiserNoiseRemover, 3);
        Assert.Equal(0f, vm.DenoiserFormantMidDb, 3);
    }

    [Fact]
    public void ResetToTheSameValueDoesNotDirtyThePreset()
    {
        int dirty = 0;
        var vm = Strips.Microphone(new FakeAudioEngine(), markDirty: () => dirty++);

        vm.VolumeDb = -5f;
        int afterEdit = dirty;

        vm.ResetVolumeCommand.Execute(null);
        int afterReset = dirty;

        vm.ResetVolumeCommand.Execute(null);

        Assert.Equal(0f, vm.VolumeDb);
        // Повторный щелчок по уже сброшенному регулятору не должен писать
        // settings.json: пользователь ничего не менял.
        Assert.Equal(afterEdit + 1, afterReset);
        Assert.Equal(afterReset, dirty);
    }
}
