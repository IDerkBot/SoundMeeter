using SoundMeeter.Controls;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views;
using SoundMeeter.Views.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Кнопка EQ на стрипе и окно его настроек (SM-B05).
///
/// Кнопка проверяется как кнопка: ЛКМ включает эффект и пишет это в пресет, ПКМ
/// открывает окно. Окно проверяется как содержимое: кривая получает те же полосы,
/// что и модель, и крутилки показывают сохранённые значения, а не нули — привязка
/// float (модель) → double (свойство элемента) может не сработать молча.
/// </summary>
public class EqualizerWindowTests
{
    [Fact]
    public void TheStripHasAnEqualizerWithOneEntryPerBand()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());

        Assert.NotNull(vm.Equalizer);
        Assert.Equal(InputChannelModel.EqBandCount, vm.Equalizer.Bands.Count);
        Assert.Equal(3, vm.Equalizer.Knobs.Count);   // makeup-gain и два среза
    }

    [Fact]
    public void ClickingTheEqButtonTogglesTheEffect()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var strip = new InputStripView { DataContext = vm };
            VisualTree.Layout(strip, 200, 600);
            var eq = VisualTree.FindToggle(strip, "EqBtn")!;

            VisualTree.Click(eq);
            Assert.True(vm.EqEnabled);
            Assert.True(vm.Model.EqEnabled);
            Assert.True(eq.IsChecked);

            VisualTree.Click(eq);
            Assert.False(vm.Model.EqEnabled);
            Assert.False(eq.IsChecked);
        });
    }

[Fact]
    public void TheEqualizerFollowsTheStripToggle()
    {
        // Включением эквалайзера управляют не только кнопка стрипа: MIDI и
        // пресет меняют то же поле. Кнопка обязана показывать актуальное
        // состояние, а окно — гаснуть приглушением кривой.
        var vm = Strips.Microphone(new FakeAudioEngine());
        var eq = vm.Equalizer;
        string off = eq.ToolTip;

        vm.EqEnabled = true;
        Assert.True(eq.IsEnabled);

        // Подсказка сравнивается сама с собой, а не по слову из ресурсов: язык
        // подсказки зависит от языка системы, и проверка должна быть про переключение,
        // а не про конкретную формулировку перевода.
        Assert.NotEqual(off, eq.ToolTip);

        eq.IsEnabled = false;
        Assert.False(vm.EqEnabled);
        Assert.False(vm.Model.EqEnabled);
    }

    [Fact]
    public void TheEqualizerOffersTheCurvePresetsAndAppliesThemOnPick()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var eq = vm.Equalizer;

            Assert.Equal(5, eq.Presets.Count);

            // Список пуст, пока кривая пользовательская: показывать первый
            // пресет значило бы утверждать, что под текущей кривой он лежит.
            Assert.Null(eq.SelectedPreset);

            eq.SelectedPreset = eq.Presets[0];

            var preset = eq.Presets[0].Preset;
            for (int i = 0; i < InputChannelModel.EqBandCount; i++)
                Assert.Equal(preset.BandGainsDb[i], vm.Model.GetEqBand(i), 2);

            Assert.Equal(preset.PreampDb, vm.Model.EqPreampDb, 2);

            // Выбранный пресет обязан быть виден в окне: иначе список молча
            // применил бы кривую и оставил подпись, не относящуюся к ней.
            var window = new EqualizerWindow(eq);
            try
            {
                window.Show();
                window.UpdateLayout();

                var combo = VisualTree.FindAll<ComboBox>(window).Single();
                Assert.Equal(5, combo.Items.Count);
                Assert.Same(eq.Presets[0], combo.SelectedItem);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheCutKnobsUseTheLogarithmicScale()
    {
        // Причина, по которой у ползунка среза 3…20 кГц есть отдельная шкала:
        // на линейной до края не доехать, а колесо с шагом в 1 Гц на 17 тысячах
        // размаха не двигает ничего. Уровень и makeup-gain остаются линейными.
        var vm = Strips.Microphone(new FakeAudioEngine());
        var eq = vm.Equalizer;

        Assert.False(eq.Preamp.IsLogarithmic);
        Assert.True(eq.LowCut.IsLogarithmic);
        Assert.True(eq.HighCut.IsLogarithmic);
    }

    [Fact]
    public void BandGainWritesThroughToTheModelAndClampsToItsRange()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        var band = vm.Equalizer.Bands[6];

        band.Gain = 6f;
        Assert.Equal(6f, vm.Model.EqBandGains[6], 2);

        // Полоса не должна позволить записать в пресет значение, которое DSP
        // потом срежет: границы VM и обработки обязаны совпадать.
        band.Gain = 999f;
        Assert.Equal(12f, vm.Model.EqBandGains[6], 2);
        band.Gain = float.NaN;
        Assert.Equal(0f, vm.Model.EqBandGains[6], 2);
    }

    [Fact]
    public void ResetAllFlattensTheCurveAndTurnsTheCutsOff()
    {
        var vm = Strips.Microphone(new FakeAudioEngine());
        vm.Model.SetEqBand(0, 8f);
        vm.Model.EqPreampDb = -4f;
        vm.Model.EqLowCutHz = 120f;

        vm.Equalizer.FlatCommand.Execute(null);

        Assert.All(vm.Equalizer.Bands, band => Assert.Equal(0f, band.Gain));
        Assert.Equal(0f, vm.Model.EqPreampDb);
        Assert.Equal(InputChannelModel.EqLowCutMinHz, vm.Model.EqLowCutHz);
    }

    [Fact]
    public void RightClickOpensOneWindowPerStrip()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var owner = ShowStrip(vm, out var strip);
            try
            {
                VisualTree.RightClick(VisualTree.FindToggle(strip, "EqBtn")!);

                var opened = owner.OwnedWindows.OfType<EqualizerWindow>().ToArray();
                Assert.Single(opened);
                Assert.Equal(vm.Id, opened[0].StripId);

                // Повторный ПКМ по тому же стрипу должен поднять уже открытое
                // окно, а не открыть второе такой же кривой.
                VisualTree.RightClick(VisualTree.FindToggle(strip, "EqBtn")!);
                Assert.Single(owner.OwnedWindows.OfType<EqualizerWindow>());
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void TheWindowClosesWhenTheStripIsRecreated()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var owner = ShowStrip(vm, out var strip);
            try
            {
                VisualTree.RightClick(VisualTree.FindToggle(strip, "EqBtn")!);
                Assert.Single(owner.OwnedWindows.OfType<EqualizerWindow>());

                // Стрипы пересоздаются на каждом осмотре каталога: у нового
                // стрипа уже нет ни источника, ни обработки, и держать его окно
                // незачем.
                vm.Dispose();
                Assert.Empty(owner.OwnedWindows.OfType<EqualizerWindow>());
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void TheWindowShowsTheCurveWithTheBandsAndKnobs()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            vm.Model.SetEqBand(6, 7.5f);
            vm.Model.EqPreampDb = -3f;
            vm.Model.EqLowCutHz = 120f;

var window = new EqualizerWindow(vm.Equalizer);
            try
            {
                // Окно показываем, а не раскладываем в отрыве: содержимое Window
                // не строится, пока не применён его шаблон.
                window.Show();
                window.UpdateLayout();

                var curves = VisualTree.FindAll<EqualizerCurveView>(window);
                Assert.Single(curves);
                Assert.NotNull(curves[0].Bands);
                Assert.Equal(InputChannelModel.EqBandCount, curves[0].Bands!.Count);

                // Крутилки окна показывают сохранённые значения, а не нули:
                // привязка float (модель) → double (свойство элемента) молча
                // не срабатывает, и пользователь увидел бы ровную кривую.
                var knobs = VisualTree.FindAll<GainKnob>(window);
                Assert.Equal(3, knobs.Count);
                Assert.Equal(-3f, (float)knobs[0].Value, 2);
                Assert.Equal(120f, (float)knobs[1].Value, 0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheCurveActuallyDrawsTheResponse()
    {
        UiHost.Run(() =>
        {
            var vm = Strips.Microphone(new FakeAudioEngine());
            var curve = new EqualizerCurveView
            {
                Bands = new System.Collections.ObjectModel.ObservableCollection<EqBandViewModel>(vm.Equalizer.Bands)
            };
            VisualTree.Layout(curve, 560, 300);

            // Ровная кривая и пустой фон неразличимы по «факту отрисовки», но
            // ответ на вопрос «рисуется ли вообще» — да: без вызова OnRender
            // контрол был бы пустым холстом, и ошибка в расчёте АЧХ или в
            // отрисовке подписей прошла бы незамеченной.
            var bitmap = new RenderTargetBitmap(
                (int)curve.ActualWidth, (int)curve.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(curve);

            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

            Assert.Contains(pixels, value => value != pixels[0]);
        });
    }

    /// <summary>
    /// Стрип внутри настоящего окна: <c>OnEqRightClick</c> ищет владельца через
    /// <see cref="Window.GetWindow"/>, а <c>OwnedWindows</c> заполняется только у
    /// показанного окна. Поэтому здесь нужен <c>Show</c>, а не раскладка в
    /// отрыве от окна.
    /// </summary>
    private static Window ShowStrip(InputChannelViewModel vm, out InputStripView strip)
    {
        strip = new InputStripView { DataContext = vm };
        var owner = new Window { Width = 900, Height = 700, Content = strip };
        owner.Show();
        strip.UpdateLayout();
        return owner;
    }
}