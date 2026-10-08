using SoundMeeter.Audio;
using SoundMeeter.Controls;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Крутилка <see cref="GainKnob"/>: две шкалы, линейная и логарифмическая.
///
/// Проверяется колесом мыши, а не перетаскиванием: шаг перетаскивания задаётся
/// координатой курсора, которой у синтетического события нет, а колесо от
/// координаты не зависит — и жалоба была именно про скорость, то есть про шаг.
/// </summary>
public class GainKnobTests
{
    /// <summary>Диапазон среза сверху: те самые 17 тысяч Гц, из-за которых ползунок
    /// и стал логарифмическим.</summary>
    private static GainKnob HighCut()
    {
        var knob = new GainKnob
        {
            Minimum = InputChannelModel.EqHighCutMinHz,
            Maximum = InputChannelModel.EqHighCutMaxHz,
            IsLogarithmic = true,
            Value = InputChannelModel.EqHighCutMaxHz
        };
        VisualTree.Layout(knob, 56, 56);
        return knob;
    }

    [Fact]
    public void ALinearKnobKeepsMovingInDecibels()
    {
        UiHost.Run(() =>
        {
            var knob = new GainKnob { Minimum = 0, Maximum = 60, Value = 30 };
            VisualTree.Layout(knob, 56, 56);

            VisualTree.Wheel(knob);
            Assert.Equal(31d, knob.Value, 3);

            VisualTree.Wheel(knob, -1);
            Assert.Equal(30d, knob.Value, 3);
        });
    }

    [Fact]
    public void TheHighCutRangeIsReachableInAFewWheelNotches()
    {
        UiHost.Run(() =>
        {
            // На линейной шкале тот же диапазон стоил бы 17 000 щелчков колеса
            // (шаг 1 Гц) или 34 000 px перетаскивания (2 px на герц) — это и была
            // жалоба. В логарифмической шкале диапазон проезжается за десяток.
            var knob = HighCut();

            int clicks = 0;
            while (knob.Value > InputChannelModel.EqHighCutMinHz + 1)
            {
                VisualTree.Wheel(knob, -1);
                clicks++;
                Assert.True(clicks < 100, "ползунок не доехал до низа");
            }

            Assert.InRange(clicks, 1, 10);
            Assert.Equal(InputChannelModel.EqHighCutMinHz, knob.Value, 0);
        });
    }

    [Fact]
    public void ALinearKnobOfTheSameRangeIsHopeless()
    {
        UiHost.Run(() =>
        {
            // Не проверка «так и было», а объяснение, почему у срезов отдельная
            // шкала: если IsLogarithmic когда-нибудь уберут, проверка выше упадёт,
            // а эта покажет, сколько стоило это решение.
            var knob = new GainKnob
            {
                Minimum = InputChannelModel.EqHighCutMinHz,
                Maximum = InputChannelModel.EqHighCutMaxHz,
                Value = InputChannelModel.EqHighCutMaxHz
            };
            VisualTree.Layout(knob, 56, 56);

            for (int i = 0; i < 1000; i++) VisualTree.Wheel(knob, -1);

            Assert.True(knob.Value > 18000, $"за 1000 щелчков уехал только до {knob.Value:F0}");
        });
    }

    [Fact]
    public void TheLogarithmicKnobSpentItsWholeRangeEvenly()
    {
        UiHost.Run(() =>
        {
            // Смысл логарифмической шкалы — одинаковая доля диапазона на щелчок
            // колеса, а не «просто быстрее». От нижнего края вверх и от верхнего
            // вниз один щелчок должен давать одно и то же отношение: только так
            // частота, от которой зависит разрядность фильтра, двигается
            // равномерно на слух.
            var knob = HighCut();

            VisualTree.Wheel(knob, -1);
            double down = InputChannelModel.EqHighCutMaxHz / knob.Value;

            knob.Value = InputChannelModel.EqHighCutMinHz;
            VisualTree.Wheel(knob);
            double up = knob.Value / InputChannelModel.EqHighCutMinHz;

            Assert.Equal(down, up, 3);
        });
    }

    [Fact]
    public void ALinearKnobIsUnusableBelowAUnitStep()
    {
        UiHost.Run(() =>
        {
            // Нижняя граница не может быть нулём: ln такого значения не
            // существует. Такой параметр крутилка обслуживает линейной шкалой,
            // иначе она уехала бы в −∞ на самом краю дуги.
            var knob = new GainKnob
            {
                Minimum = 0,
                Maximum = 100,
                IsLogarithmic = true,
                Value = 0
            };
            VisualTree.Layout(knob, 56, 56);

            VisualTree.Wheel(knob);

            Assert.InRange(knob.Value, 0d, 100d);
        });
    }
}