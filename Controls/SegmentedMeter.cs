using System.Windows;
using System.Windows.Media;

namespace SoundMeeter.Controls;

/// <summary>
/// Светодиодный VU-метр в стиле VoiceMeeter: матрица сегментов
/// (по умолчанию 2 колонки × 40 рядов), зажигается снизу вверх
/// по уровню. Логарифмическая шкала, цвет от зелёного к красному.
/// Считает сегменты сам в OnRender — без десятков биндингов.
/// </summary>
public sealed class SegmentedMeter : FrameworkElement
{
    private static readonly Brush UnlitBrush = MakeBrush(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Brush PeakOverlayBrush = MakeBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly SolidColorBrush[] LitBrushes = BuildLitBrushes();

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(float),
        typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(int),
        typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(40, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender));

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        int seg = Segments;
        int cols = Columns;
        double w = ActualWidth;
        double h = ActualHeight;
        if (seg <= 0 || cols <= 0 || w <= 0 || h <= 0) return;

        const double gap = 1.0;
        double cellW = (w - gap * (cols - 1)) / cols;
        double cellH = (h - gap * (seg - 1)) / seg;
        if (cellW <= 0 || cellH <= 0) return;

        int lit = LitCountFor(Level, seg);
        double step = 1.0 / (seg - 1);

        for (int c = 0; c < cols; c++)
        {
            double x = c * (cellW + gap);
            for (int i = 0; i < seg; i++)
            {
                bool on = i < lit;
                double y = h - (i + 1) * cellH - i * gap;

                if (on)
                {
                    int idx = (int)Math.Round(i * step * (LitBrushes.Length - 1));
                    dc.DrawRectangle(LitBrushes[Math.Clamp(idx, 0, LitBrushes.Length - 1)], null, new Rect(x, y, cellW, cellH));
                    if (i == lit - 1)
                        dc.DrawRectangle(PeakOverlayBrush, null, new Rect(x, y, cellW, cellH));
                }
                else
                {
                    dc.DrawRectangle(UnlitBrush, null, new Rect(x, y, cellW, cellH));
                }
            }
        }
    }

    private static int LitCountFor(float level, int seg)
    {
        if (level <= 0.0001f) return 0;

        double db = 20.0 * Math.Log10(level);
        double normalized = (db + 60.0) / 66.0;
        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        return Math.Clamp((int)Math.Ceiling(normalized * seg), 0, seg);
    }

    private static SolidColorBrush[] BuildLitBrushes()
    {
        const int count = 64;
        var arr = new SolidColorBrush[count];
        for (int i = 0; i < count; i++)
            arr[i] = MakeBrush(ColorFromPosition(i / (double)(count - 1)));
        return arr;
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Цвет сегмента по его позиции от низа (0) к верху (1): зелёный → жёлтый → красный.</summary>
    private static Color ColorFromPosition(double t)
    {
        var stops = new[]
        {
            (0.00, Color.FromRgb(0x22, 0xB1, 0x4C)),
            (0.50, Color.FromRgb(0x9E, 0xCB, 0x2D)),
            (0.74, Color.FromRgb(0xF8, 0xE7, 0x1C)),
            (0.88, Color.FromRgb(0xF5, 0x8C, 0x00)),
            (1.00, Color.FromRgb(0xED, 0x1C, 0x24)),
        };

        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].Item1)
            {
                double p = (stops[i].Item1 - stops[i - 1].Item1) <= 0
                    ? 0
                    : (t - stops[i - 1].Item1) / (stops[i].Item1 - stops[i - 1].Item1);
                return Lerp(stops[i - 1].Item2, stops[i].Item2, p);
            }
        }
        return stops[^1].Item2;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}