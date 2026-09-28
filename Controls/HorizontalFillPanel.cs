using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Controls
{
    /// <summary>
    /// ItemsPanel для горизонтальной ленты стрипов: элементы идут слева направо
    /// по своей ширине и каждый занимает всю высоту распорной области. Обычный
    /// StackPanel для этого не годится — он мержит детей по DesiredSize и прижимает
    /// к верху, поэтому стрипы не добирали высоту окна, а пустое место оставалось
    /// под лентой. Метры при этом сохраняют свой размер: высоту добирает панель
    /// приложений внутри стрипа.
    /// </summary>
    public class HorizontalFillPanel : Panel
    {
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

            foreach (UIElement child in InternalChildren)
            {
                var width = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }
}
