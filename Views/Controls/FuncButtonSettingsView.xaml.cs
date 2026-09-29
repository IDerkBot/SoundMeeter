using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Содержимое попапа назначения кнопки FUNC (ПКМ по FUNC1/FUNC2 на входном
    /// стрипе). DataContext — FuncButtonViewModel.
    ///
    /// Подписка на StateToggled сделана на code-behind, а не в разметке: имя
    /// обработчика из общего XAML-шаблона указать нельзя, а событие нужно снять,
    /// когда попап переоткрывается с другим стрипом — иначе старый
    /// FuncButtonViewModel продолжал бы держать ссылку на закрытое окно.
    /// </summary>
    public partial class FuncButtonSettingsView : UserControl
    {
        public FuncButtonSettingsView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// Правило применено или снято — попап пора закрыть, чтобы был виден
        /// результат (нажатая кнопка и метры стрипа).
        /// </summary>
        public event EventHandler? CloseRequested;

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is FuncButtonViewModel old) old.StateToggled -= OnStateToggled;
            if (e.NewValue is FuncButtonViewModel now) now.StateToggled += OnStateToggled;
        }

        private void OnStateToggled(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
