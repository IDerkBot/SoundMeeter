using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;

namespace SoundMeeter.Views
{
    /// <summary>
    /// Окно настроек эквалайзера стрипа (SM-B05): кривая с десятью полосами,
    /// общий makeup-gain и два среза. DataContext — EqualizerViewModel стрипа.
    ///
    /// Окно не модальное и не владеет данными: все правки идут прямо в пресет,
    /// поэтому закрытие ничего не отменяет, а звук слышно всё это время.
    /// </summary>
    public partial class EqualizerWindow : Window
    {
        /// <summary>
        /// Id стрипа, чей эквалайзер открыт. Нужен, чтобы второй ПКМ по EQ того же
        /// стрипа поднял уже открытое окно, а не открыл ещё одно такой же кривой.
        /// </summary>
        public string StripId { get; }

        public EqualizerWindow(EqualizerViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);

            InitializeComponent();
            Language = LocResources.XmlLanguage;
            DataContext = viewModel;
            StripId = viewModel.StripId;

            // Стрипы пересоздаются на каждом осмотре каталога: у удалённого
            // стрипа больше нет ни источника, ни обработки, и держать его окно
            // значит держать настройки несуществующего канала.
            viewModel.Invalidated += OnStripInvalidated;
            Closed += (_, _) => viewModel.Invalidated -= OnStripInvalidated;
        }

        private void OnStripInvalidated(object? sender, EventArgs e) => Close();

        /// <summary>
        /// Закрытие окна. IsCancel="True" в разметке отвечает только за ESC и сам
        /// по себе не закрывает окно по клику — кнопке нужен обработчик, как в
        /// ObsDockSettingsWindow и MidiBindingsWindow.
        /// </summary>
        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}