using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Controls
{
    /// <summary>
    /// Крутилка (rotary knob) для входного усиления. Своего контрола такого рода
    /// в проекте нет: второй вертикальный фейдер на стрипе микрофона вводит в
    /// заблуждение (это разные параметры), а дугу с указателем дешевле нарисовать
    /// напрямую, чем городить шаблон поверх Slider.
    ///
    /// Управление: тянуть мышью вверх-вниз (1 дБ на 2 px), колесо мыши или стрелки
    /// (±1 дБ). Значение — обычный DP с двусторонней привязкой, так что контрол
    /// годится не только для усиления.
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

        private readonly Pen _trackPen;
        private readonly Pen _valuePen;
        private readonly Pen _pointerPen;

        private double _dragStartY;
        private double _dragStartValue;
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
        }

        /// <summary>Положение значения в диапазоне, 0…1 (вне диапазона зажато).</summary>
        private double Normalized(double value)
        {
            double span = Maximum - Minimum;
            if (span <= 0) return 0;
            return Math.Clamp((value - Minimum) / span, 0, 1);
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

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStartY = e.GetPosition(this).Y;
            _dragStartValue = Value;
            CaptureMouse();
            Focus();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;

            SetFromInput(_dragStartValue + (_dragStartY - e.GetPosition(this).Y) / PixelsPerDb, FineStepDb);
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
            SetFromInput(Value + Math.Sign(e.Delta) * StepDb, FineStepDb);
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Right:
                    SetFromInput(Value + StepDb, FineStepDb);
                    break;
                case Key.Down:
                case Key.Left:
                    SetFromInput(Value - StepDb, FineStepDb);
                    break;
                case Key.Home:
                    SetFromInput(Minimum, StepDb);
                    break;
                case Key.End:
                    SetFromInput(Maximum, StepDb);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }
    }
}
