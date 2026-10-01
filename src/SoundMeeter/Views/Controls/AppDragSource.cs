using System.Windows;
using System.Windows.Input;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Источник перетаскивания приложения для списков карточек. Держит точку
    /// нажатия и по первому смещению мыши запускает DragDrop, поэтому
    /// одинаково работает в стрипе и в панелях приложений.
    /// </summary>
    internal sealed class AppDragSource
    {
        private Point? _pressStart;

        public void Begin(IInputElement owner, MouseButtonEventArgs e) => _pressStart = e.GetPosition(owner);

        /// <summary>Сбросить ожидание перетаскивания (например, по клику «удалить правило»).</summary>
        public void Cancel() => _pressStart = null;

        public void Move(IInputElement owner, FrameworkElement element, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressStart == null) return;

            var delta = e.GetPosition(owner) - _pressStart.Value;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _pressStart = null;
            var app = element.DataContext;
            if (!MixerUi.CanDragApp(app)) return;

            MixerUi.StartAppDrag(element, app!, MixerUi.FindMainViewModel(element));
        }
    }
}
