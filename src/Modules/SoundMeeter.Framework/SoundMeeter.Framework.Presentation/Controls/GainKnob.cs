using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Framework.Presentation.Controls;

/// <summary>
/// Крутилка (rotary knob) для входного усиления. Своего контрола такого рода
/// в проекте нет: второй вертикальный фейдер на стрипе микрофона вводит в
/// заблуждение (это разные параметры), а дугу с указателем дешевле нарисовать
/// напрямую, чем городить шаблон поверх Slider.
///
/// Управление: тянуть мышью вверх-вниз (1 дБ на 2 px), колесо мыши или стрелки
/// (±1 дБ). Значение — обычный DP с двусторонней привязкой, так что контрол
/// годится не только для усиления.
///
/// Частотные параметры (срезы, гашение) включают <see cref="IsLogarithmic"/>:
/// на линейной шкале размах 3…20 кГц — это 17 тысяч единиц, и до верха
/// ползунок не доехал бы за всю жизнь перетаскивания.
/// </summary>
public class GainKnob : Control
{
    // Дуга рисуется не на все 360°, а на 270°: у регуляторов с «мёртвой» зоной
    // внизу указатель так не упирается в край.
    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    private const double TrackThickness = 5;
    private const double PixelsPerDb = 2.0;
    private const double StepDb = 1.0;
    private const double FineStepDb = 0.5;

    /// <summary>
    /// Доля шкалы на один пиксель в логарифмическом режиме: весь диапазон
    /// проезжается за 200 px. Это в полтора раза медленнее линейной шкалы на
    /// Gain 0…60 дБ (там весь диапазон — 120 px), и это правильно: у
    /// частотного параметра шаг в 1 Гц на пиксель давал бы рывки в сотни
    /// герц, а здесь на пиксель приходится доля диапазона.
    /// </summary>
    private const double LogFractionPerPixel = 1.0 / 200.0;

    /// <summary>
    /// Шаг колеса мыши в логарифмическом режиме — восьмая часть диапазона: от
    /// 3 до 20 кГц это восемь щелчков вместо десяти тысяч. Тонкая правка
    /// делается перетаскиванием, а двойной щелчок возвращает «выключено».
    /// </summary>
    private const double LogWheelFraction = 1.0 / 8.0;

    private readonly Pen _trackPen;
    private readonly Pen _valuePen;
    private readonly Pen _pointerPen;

    private double _dragStartY;
    private double _dragStartValue;
    private double _dragStartPosition;
    private bool _dragging;

    static GainKnob()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(GainKnob), new FrameworkPropertyMetadata(typeof(GainKnob)));
    }

    public GainKnob()
    {
        // Дорожка светлее фона стрипа (#3E3E42), иначе шкала не читается.
        _trackPen = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A)), TrackThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _valuePen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xE0)), TrackThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _pointerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        Focusable = true;
        Cursor = Cursors.Hand;
        // Transparent (а не null) — на случай, если шаблон когда-нибудь появится:
        // тогда Border шаблона будет hit-testable. А пока HitTestCore ниже
        // гарантирует, что колесо мыши работает по всей площади контрола.
        Background = Brushes.Transparent;

        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        AddHandler(MouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        AddHandler(MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown));

        SizeChanged += (_, _) => InvalidateVisual();
    }

    #region Свойства

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GainKnob),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((GainKnob)d).InvalidateVisual()));

    /// <summary>Текущее значение параметра (дБ).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(GainKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(GainKnob),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty IsLogarithmicProperty = DependencyProperty.Register(
        nameof(IsLogarithmic), typeof(bool), typeof(GainKnob),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Шкала логарифмическая, а не линейная.
    ///
    /// Нужна там, где важно отношение, а не разность: у частоты среза размах
    /// 20…300 Гц это пятнадцатикратное изменение, и на линейной шкале низкие
    /// частоты занимают первые шесть процентов дуги (ими не попасть), а верхние
    /// пролетаются за одно движение мыши. Доля параметра на равном угле дуги
    /// вместо единицы значения здесь и есть то, что делает крутилку пригодной.
    /// </summary>
    public bool IsLogarithmic
    {
        get => (bool)GetValue(IsLogarithmicProperty);
        set => SetValue(IsLogarithmicProperty, value);
    }

    public static readonly DependencyProperty TextFormatProperty = DependencyProperty.Register(
        nameof(TextFormat), typeof(string), typeof(GainKnob),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Формат отображения значения (например, "{0:F1} dB"). Если null или пустой,
    /// текст не отображается.
    /// </summary>
    public string TextFormat
    {
        get => (string)GetValue(TextFormatProperty);
        set => SetValue(TextFormatProperty, value);
    }

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(GainKnob),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Цвет текста значения.</summary>
    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize), typeof(double), typeof(GainKnob),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Размер шрифта текста значения.</summary>
    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    #endregion

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double side = Math.Min(ActualWidth, ActualHeight);
        if (side < 8) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = side / 2 - TrackThickness / 2 - 1;

        dc.DrawGeometry(null, _trackPen, Arc(center, radius, StartAngle, SweepAngle));

        double t = Normalized(Value);
        if (t > 0.0001)
            dc.DrawGeometry(null, _valuePen, Arc(center, radius, StartAngle, SweepAngle * t));

        // Указатель: от края дуги к центру, по текущему углу значения.
        double angle = (StartAngle + SweepAngle * t) * Math.PI / 180;
        var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
        dc.DrawLine(_pointerPen,
            center + dir * (radius - TrackThickness),
            center + dir * (radius - TrackThickness - 6));

        // Отображение текста значения в центре контрола.
        if (!string.IsNullOrEmpty(TextFormat))
        {
            var text = string.Format(CultureInfo.CurrentCulture, TextFormat, Value);
            var foreground = TextForeground ?? Brushes.White;
            var typeface = new Typeface("Segoe UI");
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                TextFontSize,
                foreground,
                1.0);

            var textOrigin = new Point(
                center.X - formattedText.Width / 2,
                center.Y - formattedText.Height / 2);

            dc.DrawText(formattedText, textOrigin);
        }
    }

    /// <summary>
    /// Весь прямоугольник контрола участвует в hit-test, а не только
    /// нарисованные пиксели (дуга, текст). Иначе <see cref="UIElement.MouseWheel"/>
    /// и другие события мыши не доходили бы до контрола, когда курсор над
    /// пустым фоном: для Control без явного шаблона <c>Background = Transparent</c>
    /// недостаточно — шаблон не отрисовывает Border, и hit-test работает только
    /// по содержимому <c>OnRender</c>. Это переопределение решает проблему
    /// надёжно, независимо от наличия шаблона.
    /// </summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    /// <summary>Положение значения на шкале, 0…1 (вне диапазона зажато).</summary>
    private double Normalized(double value)
    {
        double span = Maximum - Minimum;
        if (span <= 0) return 0;

        if (!IsLogarithmic) return Math.Clamp((value - Minimum) / span, 0, 1);

        // Нижняя граница не может быть нулём или меньше: ln такого значения не
        // существует. Параметр с нулевым минимумом логарифмической шкалой не
        // обслуживается — для него остаётся линейная.
        if (Minimum <= 0 || Maximum <= 0) return Math.Clamp((value - Minimum) / span, 0, 1);

        double lo = Math.Log(Minimum);
        double hi = Math.Log(Maximum);
        return hi - lo < 1e-9 ? 0 : Math.Clamp((Math.Log(value) - lo) / (hi - lo), 0, 1);
    }

    /// <summary>Значение по положению на шкале, 0…1 — обратная к <see cref="Normalized"/>.</summary>
    private double FromNormalized(double position)
    {
        double clamped = Math.Clamp(position, 0, 1);
        double span = Maximum - Minimum;
        if (span <= 0) return Minimum;

        if (!IsLogarithmic || Minimum <= 0 || Maximum <= 0) return Minimum + clamped * span;

        double lo = Math.Log(Minimum);
        return Math.Exp(lo + clamped * (Math.Log(Maximum) - lo));
    }

    private static Geometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        if (radius <= 0) return Geometry.Empty;

        var start = startDeg * Math.PI / 180;
        var end = (startDeg + sweepDeg) * Math.PI / 180;

        var from = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var to = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));

        // Дуга длиннее полуокружности требует флага LargeArc, иначе WPF нарисует
        // короткую дугу по другой стороне круга.
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            to,
            new Size(radius, radius),
            0,
            Math.Abs(sweepDeg) > 180,
            SweepDirection.Clockwise,
            true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Запись значения с зажатием диапазона и округлением до шага.</summary>
    private void SetFromInput(double value, double step)
    {
        if (!double.IsFinite(value)) return;

        double clamped = Math.Clamp(value, Minimum, Maximum);
        double snapped = step > 0 ? Math.Round(clamped / step) * step : clamped;
        if (Math.Abs(snapped - Value) < 1e-6) return;

        Value = Math.Clamp(snapped, Minimum, Maximum);
    }

    /// <summary>
    /// Запись значения с зажатием диапазона, без округления. Так ведёт себя
    /// логарифмическая шкала: делений на ней нет, и привязка к «круглым»
    /// значениям только мешала бы тянуть плавно.
    /// </summary>
    private void SetValue(double value)
    {
        if (!double.IsFinite(value)) return;

        double clamped = Math.Clamp(value, Minimum, Maximum);
        if (Math.Abs(clamped - Value) < 1e-9) return;

        Value = clamped;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartValue = Value;
        _dragStartPosition = Normalized(Value);
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;

        double travelled = _dragStartY - e.GetPosition(this).Y;

        if (IsLogarithmic && Minimum > 0 && Maximum > 0)
            SetValue(FromNormalized(_dragStartPosition + travelled * LogFractionPerPixel));
        else
            SetFromInput(_dragStartValue + travelled / PixelsPerDb, FineStepDb);

        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double step = Math.Sign(e.Delta);

        if (IsLogarithmic && Minimum > 0 && Maximum > 0)
            SetValue(FromNormalized(Normalized(Value) + step * LogWheelFraction));
        else
            SetFromInput(Value + step * StepDb, FineStepDb);

        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Right:
                Nudge(1);
                break;
            case Key.Down:
            case Key.Left:
                Nudge(-1);
                break;
            case Key.Home:
                SetValue(Minimum);
                break;
            case Key.End:
                SetValue(Maximum);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Шаг на одну «ступеньку» — тот же, что и у колеса мыши.</summary>
    private void Nudge(int direction)
    {
        if (IsLogarithmic && Minimum > 0 && Maximum > 0)
            SetValue(FromNormalized(Normalized(Value) + direction * LogWheelFraction));
        else
            SetFromInput(Value + direction * StepDb, FineStepDb);
    }
}