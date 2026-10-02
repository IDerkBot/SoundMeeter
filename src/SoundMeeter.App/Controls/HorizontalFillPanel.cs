using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundMeeter.Controls
{
    /// <summary>Аргументы запроса на перестановку элементов ленты.</summary>
    public sealed class ReorderEventArgs : EventArgs
    {
        public ReorderEventArgs(int fromIndex, int toIndex)
        {
            FromIndex = fromIndex;
            ToIndex = toIndex;
        }

        /// <summary>Индекс перетаскиваемого элемента.</summary>
        public int FromIndex { get; }

        /// <summary>Индекс, на который элемент встанет (уже с учётом снятия с прежнего места).</summary>
        public int ToIndex { get; }
    }

    /// <summary>
    /// ItemsPanel для горизонтальной ленты стрипов: элементы идут слева направо
    /// по своей ширине и каждый занимает всю высоту распорной области. Обычный
    /// StackPanel для этого не годится — он мержит детей по DesiredSize и прижимает
    /// к верху, поэтому стрипы не добирали высоту окна, а пустое место оставалось
    /// под лентой. Метры при этом сохраняют свой размер: высоту добирает панель
    /// приложений внутри стрипа.
    ///
    /// Панель же отвечает за перетаскивание: она знает границы каждого элемента,
    /// поэтому может показать линию вставки и посчитать позицию без гадания по
    /// координатам соседей. Саму перестановку выполняет обработчик
    /// <see cref="ReorderRequested"/> — панель о списках стрипов ничего не знает.
    /// </summary>
    public class HorizontalFillPanel : Panel
    {
        /// <summary>Формат перетаскивания стрипа: в данных лежит Id его модели.</summary>
        public const string StripDragFormat = "SoundMeeter.Strip";

        private const double IndicatorThickness = 3;

        private readonly List<Rect> _bounds = [];
        private readonly Pen _indicatorPen;

        // Откуда начали тянуть: пока указатель не сдвинулся на столько пикселей,
        // это ещё щелчок, а не перетаскивание.
        private Point _dragOrigin;
        private bool _maybeDragging;
        private int _dropIndex = -1;
        private bool _allowReorder;

        public HorizontalFillPanel()
        {
            _indicatorPen = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), IndicatorThickness);

            AddHandler(DragOverEvent, new DragEventHandler(OnDragOver));
            AddHandler(DragLeaveEvent, new DragEventHandler(OnDragLeave));
            AddHandler(DropEvent, new DragEventHandler(OnDrop));
        }

        /// <summary>
        /// Ключ элемента для поиска перетаскиваемого стрипа среди детей. Задаётся
        /// снаружи: панель не знает про стрипы и должна доверять источнику.
        /// </summary>
        public Func<object, string?>? ItemKey { get; set; }

        /// <summary>Сработало, когда пользователь отпустил стрип в новом месте.</summary>
        public event EventHandler<ReorderEventArgs>? ReorderRequested;

        /// <summary>
        /// true — панель участвует в перетаскивании (а не просто раскладывает элементы).
        /// У ленты стрипов включено всегда, у прочих применений — нет. Заодно
        /// включает AllowDrop: у Panel он по умолчанию false, и без него панель
        /// просто не получит ни DragOver, ни Drop.
        /// </summary>
        public bool AllowReorder
        {
            get => _allowReorder;
            set
            {
                _allowReorder = value;
                AllowDrop = value;
            }
        }

        #region Перетаскивание

        /// <summary>Регистрирует начало возможного перетаскивания (из стрипа).</summary>
        public void BeginDrag(Point origin)
        {
            _maybeDragging = true;
            _dragOrigin = origin;
        }

        /// <summary>true — указатель прошёл порог, пора начинать drag&amp;drop.</summary>
        public bool DragThresholdReached(Point current)
        {
            if (!_maybeDragging) return false;
            if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
                return false;

            _maybeDragging = false;
            return true;
        }

        public void CancelDrag() => _maybeDragging = false;

        private int FindSourceIndex(IDataObject data)
        {
            if (ItemKey == null) return -1;
            if (data.GetData(StripDragFormat) is not string id) return -1;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i] as FrameworkElement;
                if (child != null && ItemKey(child.DataContext) == id) return i;
            }

            return -1;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!AllowReorder) return;

            int from = FindSourceIndex(e.Data);
            if (from < 0) return; // не стрип (например, приложение) — разбирается сам стрип

            int insert = GetInsertIndex(e.GetPosition(this).X, _bounds);
            if (insert != _dropIndex)
            {
                _dropIndex = insert;
                InvalidateVisual();
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            if (_dropIndex < 0) return;

            _dropIndex = -1;
            InvalidateVisual();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!AllowReorder) return;

            int from = FindSourceIndex(e.Data);
            if (from < 0) return;

            int insert = GetInsertIndex(e.GetPosition(this).X, _bounds);
            int to = insert > from ? insert - 1 : insert;

            _dropIndex = -1;
            InvalidateVisual();

            if (to != from) ReorderRequested?.Invoke(this, new ReorderEventArgs(from, to));

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        /// <summary>
        /// Номер границы под курсором: 0 — перед первым элементом, Count — после
        /// последнего. Граница выбирается по середине ближайшего элемента, поэтому
        /// «левую» и «правую» половины стрипа можно различать на глаз.
        /// </summary>
        internal static int GetInsertIndex(double x, IReadOnlyList<Rect> bounds)
        {
            for (int i = 0; i < bounds.Count; i++)
            {
                if (x < bounds[i].Left + bounds[i].Width / 2) return i;
            }

            return bounds.Count;
        }

        #endregion

        protected override Size MeasureOverride(Size availableSize)
        {
            // По высоте ограничиваемся распорной областью, иначе дети решили бы,
            // что места бесконечно много, и растянулись бы на весь экран.
            double height = double.IsInfinity(availableSize.Height)
                ? double.PositiveInfinity
                : availableSize.Height;

            double width = 0;
            double maxHeight = 0;

            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, height));
                width += child.DesiredSize.Width;
                maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
            }

            return new Size(width, maxHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            _bounds.Clear();

            foreach (UIElement child in InternalChildren)
            {
                var width = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                _bounds.Add(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (_dropIndex < 0 || _dropIndex > _bounds.Count) return;

            // Линия вставки рисуется по границе соседей: перед первым, между двумя
            // или после последнего.
            double x = _dropIndex < _bounds.Count
                ? _bounds[_dropIndex].Left
                : (_bounds.Count > 0 ? _bounds[^1].Right : 0);

            dc.DrawLine(_indicatorPen,
                new Point(x, 2),
                new Point(x, Math.Max(2, ActualHeight - 2)));
        }
    }
}
