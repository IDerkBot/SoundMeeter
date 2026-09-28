using SoundMeeter.Controls;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Лента стрипов: группы INPUTS и OUTPUTS с кнопками добавления и
    /// перетаскиванием стрипов внутри своей группы.
    /// DataContext — MainViewModel: команды добавления и удаления стрипов,
    /// а также RemoveCommand для стрипов берутся отсюда.
    /// </summary>
    public partial class MixerView : UserControl
    {
        public MixerView()
        {
            InitializeComponent();

            // Панель создаётся из ItemsPanelTemplate при первой раскладке, а
            // ItemsControl.ItemsPanel отдаёт именно шаблон, а не созданную панель,
            // поэтому ищем её в визуальном дереве. Если на первом проходе её ещё
            // нет (пустой список), повторяем после загрузки очереди диспетчера.
            Loaded += (_, _) => HookReorder();
        }

        private bool _hooked;

        private void HookReorder()
        {
            if (_hooked) return;

            var inputs = FindPanel(InputsList);
            var buses = FindPanel(BusesList);
            if (inputs == null && buses == null)
            {
                Dispatcher.BeginInvoke(HookReorder, System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            if (inputs != null)
            {
                inputs.ItemKey = InputKey;
                inputs.ReorderRequested += OnReorderInputs;
            }

            if (buses != null)
            {
                buses.ItemKey = BusKey;
                buses.ReorderRequested += OnReorderBuses;
            }

            _hooked = true;
        }

        private static HorizontalFillPanel? FindPanel(ItemsControl list) =>
            FindDescendant<HorizontalFillPanel>(list);

        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) return found;
                if (FindDescendant<T>(child) is { } nested) return nested;
            }

            return null;
        }

        private void OnReorderInputs(object? sender, ReorderEventArgs e) =>
            (DataContext as MainViewModel)?.MoveInput(e.FromIndex, e.ToIndex);

        private void OnReorderBuses(object? sender, ReorderEventArgs e) =>
            (DataContext as MainViewModel)?.MoveBus(e.FromIndex, e.ToIndex);

        /// <summary>Ключ входного стрипа для поиска перетаскиваемого по Id модели.</summary>
        private static string? InputKey(object? item) =>
            (item as InputChannelViewModel)?.Model.Id;

        /// <summary>Ключ выходного стрипа для поиска перетаскиваемого по Id модели.</summary>
        private static string? BusKey(object? item) =>
            (item as OutputBusViewModel)?.Model.Id;
    }
}
