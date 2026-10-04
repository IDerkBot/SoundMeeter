using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Controls
{
    /// <summary>
    /// Кривая графического эквалайзера: сетка по частоте и уровню, суммарная АЧХ
    /// и десять перетаскиваемых узлов — по одному на полосу.
    ///
    /// Рисуем напрямую, как <see cref="GainKnob"/>: кривую нельзя собрать из
    /// готовых фигур WPF — её точки считаются по той же формуле, по какой DSP
    /// считает сигнал, иначе нарисованное и слышимое разойдутся. Заодно честно
    /// выходит, что менять здесь умеет только высота узла: частоты полос заданы
    /// шагом октавы и пользователю не принадлежат.
    ///
    /// Управление: тянуть узел (1:1 с вертикальной шкалой), колесо мыши над
    /// узлом (±0.5 дБ), двойной щелчок — 0 дБ. Клавиатуры нет намеренно: рядом
    /// стоят крутилки и кнопка сброса, а на самой кривой она означала бы десять
    /// неочевидных сочетаний.
    /// </summary>
    public class EqualizerCurveView : Control
    {
        // Поля под подписи осей: слева децибелы, снизу частоты.
        private const double LeftMargin = 40;
        private const double RightMargin = 10;
        private const double TopMargin = 10;
        private const double BottomMargin = 20;

        /// <summary>Шкала по вертикали шире предельных ±12 дБ, иначе крайние полосы
        /// упираются в рамку вместе со своей подписью.</summary>
        private const double PlotGainDb = 15;

        private const double NodeRadius = 5;
        private const double HitRadius = 9;
        private const float WheelStepDb = 0.5f;

        private const float SampleRate = InputSource.SampleRate;

        private static readonly double LogMinFreq = Math.Log10(20.0);
        private static readonly double LogMaxFreq = Math.Log10(20000.0);

        private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), 1);
        private readonly Pen _gridPenFaint = new(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38)), 1);
        private readonly Pen _curvePen = new(new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xE0)), 2);
        private readonly Brush _curveFill = new SolidColorBrush(Color.FromArgb(0x22, 0x3A, 0xA0, 0xE0));
        private readonly Brush _nodeFill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        private readonly Brush _nodeFillDragged = new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xE0));
        private readonly Pen _nodePen = new(new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xE0)), 1.5);
        private readonly Brush _labelBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        private readonly Brush _valueBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private readonly Brush _plotBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        private readonly Brush _inactiveBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x1E, 0x1E, 0x1E));

        private EqBandViewModel[] _bands = [];
        private bool _attached;
        private int _dragIndex = -1;
        private double _dragStartY;
        private float _dragStartGain;

        // Буферы кривой: растут до размера поля и переиспользуются между
        // перерисовками. Перетаскивание полосы шлёт перерисовку на каждое
        // движение мыши, и новые массивы на каждом кадре давали бы ровно тот
        // мусор в куче, которого здесь можно избежать целиком.
        private float[] _freq = [];
        private float[] _response = [];
        private Point[] _points = [];
        private readonly float[] _gains = new float[InputChannelModel.EqBandCount];

        public EqualizerCurveView()
        {
            // Control, а не FrameworkElement: двойной щелчок по узлу — одно из
            // основных действий, а MouseDoubleClickEvent объявлен именно у Control.
            // Шаблон и фокус — пустые намеренно: кривая рисуется вся в OnRender, и
            // тема здесь не должна ни красить фон, ни отдавать клавиатуру.
            Template = null;
            Focusable = false;
            ClipToBounds = true;
            Cursor = Cursors.Arrow;

            AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
            AddHandler(MouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
            AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
            AddHandler(MouseDoubleClickEvent, new MouseButtonEventHandler(OnMouseDoubleClick), true);
            AddHandler(MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);

            SizeChanged += (_, _) => InvalidateVisual();

            // Полосы живут дольше окна: они часть стрипа, а окно закрывается.
            // Без отписки на Unloaded каждое открытое окно оставляло бы после
            // себя подписку на все десять полос.
            Loaded += (_, _) => AttachBands();
            Unloaded += (_, _) => DetachBands();
        }

        #region Свойства

        /// <summary>
        /// Полосы кривой. Тип намеренно общий (<see cref="IList"/>), а не
        /// <c>ObservableCollection&lt;EqBandViewModel&gt;</c>: контрол читает список
        /// и следит за полосами по <see cref="INotifyPropertyChanged"/>, поэтому ему
        /// не нужен конкретный тип коллекции — ни своей, ни чужой.
        /// </summary>
        public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
            nameof(Bands), typeof(IList), typeof(EqualizerCurveView),
            new PropertyMetadata(null, OnBandsChanged));

        public IList? Bands
        {
            get => (IList?)GetValue(BandsProperty);
            set => SetValue(BandsProperty, value);
        }

        public static readonly DependencyProperty PreampDbProperty = DependencyProperty.Register(
            nameof(PreampDb), typeof(double), typeof(EqualizerCurveView),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Общий makeup-gain, дБ: на кривой это сдвиг целиком вверх или вниз.</summary>
        public double PreampDb
        {
            get => (double)GetValue(PreampDbProperty);
            set => SetValue(PreampDbProperty, value);
        }

        public static readonly DependencyProperty LowCutHzProperty = DependencyProperty.Register(
            nameof(LowCutHz), typeof(double), typeof(EqualizerCurveView),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double LowCutHz
        {
            get => (double)GetValue(LowCutHzProperty);
            set => SetValue(LowCutHzProperty, value);
        }

        public static readonly DependencyProperty HighCutHzProperty = DependencyProperty.Register(
            nameof(HighCutHz), typeof(double), typeof(EqualizerCurveView),
            new FrameworkPropertyMetadata(20000.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double HighCutHz
        {
            get => (double)GetValue(HighCutHzProperty);
            set => SetValue(HighCutHzProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(EqualizerCurveView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Эквалайзер включён. Выключенный показывается приглушённым: настройки у
        /// него остаются, и их видно, но тот, кто смотрит на кривую, должен
        /// понимать, что сейчас она ничего не делает.
        /// </summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        #endregion

        #region Полосы

        private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (EqualizerCurveView)d;
            view.DetachBands();
            view._bands = Materialize(e.NewValue as IList);
            view.AttachBands();
            view.InvalidateVisual();
        }

        private static EqBandViewModel[] Materialize(IList? source)
        {
            if (source is null || source.Count == 0) return [];

            var bands = new List<EqBandViewModel>(source.Count);
            foreach (object? item in source)
                if (item is EqBandViewModel band) bands.Add(band);

            return bands.ToArray();
        }

        /// <summary>
        /// Подписка на полосы. Кривая перерисовывается по уведомлению, а не по
        /// событию команды: полоса меняет усиление, подпись значения и состояние
        /// команды сброса, а пользователю важно первое.
        /// </summary>
        private void AttachBands()
        {
            if (_attached) return;
            _attached = true;

            foreach (var band in _bands) band.PropertyChanged += OnBandChanged;
        }

        private void DetachBands()
        {
            if (!_attached) return;
            _attached = false;

            foreach (var band in _bands) band.PropertyChanged -= OnBandChanged;
        }

        private void OnBandChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

        #endregion

        #region Отрисовка

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var plot = PlotArea();
            dc.DrawRectangle(_plotBrush, null, plot);

            // DPI один на кадр, а не на каждую подпись: подписей у кривой два
            // десятка, и GetDpi не самый дешёвый вызов.
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            DrawGainGrid(dc, plot, pixelsPerDip);
            DrawFrequencyGrid(dc, plot, pixelsPerDip);
            if (_bands.Length == 0) return;

            DrawCurve(dc, plot);
            DrawNodes(dc, plot, pixelsPerDip);

            if (!IsActive) dc.DrawRectangle(_inactiveBrush, null, plot);
        }

        /// <summary>Поле кривой: всё, кроме полей под подписи осей.</summary>
        private Rect PlotArea() => new(
            LeftMargin,
            TopMargin,
            Math.Max(1, ActualWidth - LeftMargin - RightMargin),
            Math.Max(1, ActualHeight - TopMargin - BottomMargin));

        private void DrawGainGrid(DrawingContext dc, Rect plot, double pixelsPerDip)
        {
            for (int db = -12; db <= 12; db += 6)
            {
                double y = GainToY(db, plot);
                dc.DrawLine(db == 0 ? _gridPen : _gridPenFaint, new Point(plot.Left, y), new Point(plot.Right, y));

                FormattedText label = Label(db.ToString("+0;-0", Loc.Culture), 9, _labelBrush, pixelsPerDip);
                dc.DrawText(label, new Point(
                    Math.Max(0, plot.Left - label.Width - 5),
                    y - label.Height / 2));
            }
        }

        private void DrawFrequencyGrid(DrawingContext dc, Rect plot, double pixelsPerDip)
        {
            foreach (var band in _bands)
            {
                double x = FreqToX(band.Frequency, plot);
                dc.DrawLine(_gridPenFaint, new Point(x, plot.Top), new Point(x, plot.Bottom));

                FormattedText label = Label(band.Label, 9, _labelBrush, pixelsPerDip);
                dc.DrawText(label, new Point(
                    Math.Clamp(x - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width)),
                    plot.Bottom + 4));
            }
        }

        /// <summary>
        /// Кривая суммарной АЧХ: та же формула, что и в DSP, поэтому нарисовано
        /// ровно то, что слышно. Шаг по частоте — два пикселя.
        /// </summary>
        private void DrawCurve(DrawingContext dc, Rect plot)
        {
            int count = Math.Max(2, (int)plot.Width / 2 + 1);
            EnsureBuffers(count);

            var points = _points;
            for (int i = 0; i < count; i++)
            {
                double x = plot.Left + plot.Width * i / (count - 1);
                _freq[i] = (float)XToFreq(x, plot);
                points[i] = new Point(x, 0);
            }

            Array.Clear(_gains);
            foreach (var band in _bands)
                if (band.Index >= 0 && band.Index < _gains.Length) _gains[band.Index] = band.Gain;

            EqualizerResponse.ComputeResponse(
                _gains,
                (float)PreampDb,
                (float)LowCutHz,
                (float)HighCutHz,
                SampleRate,
                _freq,
                _response);

            for (int i = 0; i < count; i++)
                points[i].Y = GainToY(_response[i], plot);

            // Заливка под кривой: без неё провалы почти не читаются, особенно при
            // слабом контрасте линий сетки.
            var figure = new PathFigure
            {
                StartPoint = new Point(plot.Left, plot.Bottom),
                IsClosed = true,
                IsFilled = true
            };
            figure.Segments.Add(new LineSegment(points[0], true));
            figure.Segments.Add(new PolyLineSegment(points, true));
            figure.Segments.Add(new LineSegment(new Point(plot.Right, plot.Bottom), true));

            var filled = new PathGeometry();
            filled.Figures.Add(figure);
            dc.DrawGeometry(_curveFill, null, filled);
            dc.DrawGeometry(null, _curvePen, Polyline(points));
        }

        private void DrawNodes(DrawingContext dc, Rect plot, double pixelsPerDip)
        {
            for (int i = 0; i < _bands.Length; i++)
            {
                var band = _bands[i];
                var center = new Point(FreqToX(band.Frequency, plot), GainToY(band.Gain, plot));
                bool dragged = i == _dragIndex;

                dc.DrawEllipse(dragged ? _nodeFillDragged : _nodeFill, _nodePen, center, NodeRadius, NodeRadius);

                FormattedText value = Label(band.Gain.ToString("0.0", Loc.Culture), 9, _valueBrush, pixelsPerDip);

                // Подпись значения над узлом, но не за рамкой: на верхней полосе
                // текст иначе уезжал бы за поле окна.
                double x = Math.Clamp(center.X - value.Width / 2, 1, Math.Max(1, ActualWidth - value.Width - 1));
                double y = center.Y - value.Height - NodeRadius - 2;
                if (y < 1) y = center.Y + NodeRadius + 2;
                dc.DrawText(value, new Point(x, y));
            }
        }

        /// <summary>
        /// Буферы ровно под текущую ширину поля. Пересоздаются только когда ширина
        /// изменилась, то есть редко: перетаскивание полосы ширину не двигает,
        /// поэтому на каждый кадр отрисовки ничто не выделяется.
        /// </summary>
        private void EnsureBuffers(int count)
        {
            if (_points.Length == count) return;
            _freq = new float[count];
            _response = new float[count];
            _points = new Point[count];
        }

        private static Geometry Polyline(Point[] points)
        {
            var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
            figure.Segments.Add(new PolyLineSegment(points, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static FormattedText Label(string text, double size, Brush brush, double pixelsPerDip) =>
            new(text, Loc.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, pixelsPerDip);

        private static double FreqToX(float hz, Rect plot) =>
            plot.Left + plot.Width * (Math.Log10(Math.Max(1e-3f, hz)) - LogMinFreq) / (LogMaxFreq - LogMinFreq);

        private static double XToFreq(double x, Rect plot) =>
            Math.Pow(10, LogMinFreq + (x - plot.Left) / plot.Width * (LogMaxFreq - LogMinFreq));

        private static double GainToY(double db, Rect plot) =>
            plot.Bottom - plot.Height * (db + PlotGainDb) / (2 * PlotGainDb);

        private static double YToGain(double y, Rect plot) =>
            (plot.Bottom - y) / plot.Height * 2 * PlotGainDb - PlotGainDb;

        #endregion

        #region Ввод

        /// <summary>Узел под курсором (с допуском <see cref="HitRadius"/>), иначе −1.</summary>
        private int BandAt(Point point)
        {
            var plot = PlotArea();
            double best = HitRadius;
            int found = -1;

            for (int i = 0; i < _bands.Length; i++)
            {
                var center = new Point(FreqToX(_bands[i].Frequency, plot), GainToY(_bands[i].Gain, plot));
                double distance = (center - point).Length;
                if (distance > best) continue;

                best = distance;
                found = i;
            }

            return found;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            int index = BandAt(e.GetPosition(this));
            if (index < 0) return;

            _dragIndex = index;
            _dragStartY = e.GetPosition(this).Y;
            _dragStartGain = _bands[index].Gain;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            int index = _dragIndex >= 0 ? _dragIndex : BandAt(e.GetPosition(this));

            // Курсор над узлом — «рука»: иначе непонятно, что кривую можно
            // тянуть, а на ощупь её не найти.
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
            if (_dragIndex < 0) return;

            var plot = PlotArea();
            double dragged = YToGain(e.GetPosition(this).Y, plot) - YToGain(_dragStartY, plot);
            _bands[_dragIndex].Gain = _dragStartGain + (float)dragged;
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIndex < 0) return;

            _dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }

        private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int index = BandAt(e.GetPosition(this));
            if (index < 0) return;

            _bands[index].ResetCommand.Execute(null);
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            int index = BandAt(e.GetPosition(this));
            if (index < 0) return;

            _bands[index].Gain += MathF.Sign(e.Delta) * WheelStepDb;
            e.Handled = true;
        }

        #endregion
    }
}