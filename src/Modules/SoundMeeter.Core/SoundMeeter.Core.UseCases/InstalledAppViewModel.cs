using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;

namespace SoundMeeter.ViewModels
{
    /// <summary>
    /// Установленная программа из реестра — то, что пользователь может
    /// перетащить на стрип, чтобы заранее направить её звук.
    ///
    /// Значок рисует UI по <see cref="IconPath"/> (см. замечание про значки в
    /// <see cref="AppViewModel"/>). Раньше ViewModel сам грузил файл значка или
    /// дёргал <c>SHGetFileInfo</c> и отдавал <c>BitmapImage</c> — это WPF в ядре
    /// и третья копия одного и того же P/Invoke.
    /// </summary>
    public partial class InstalledAppViewModel : ObservableObject
    {
        [ObservableProperty]
        private InstalledApp _app = new();

        public InstalledAppViewModel() { }

        public InstalledAppViewModel(InstalledApp app)
        {
            _app = app;
        }

        /// <summary>
        /// Откуда UI возьмёт значок: <c>DisplayIcon</c> из реестра, а если его
        /// нет — исполняемый файл. Дальше разбирается конвертер: <c>.ico</c>
        /// грузится как файл, всё остальное — через оболочку.
        /// </summary>
        public string IconPath => App.IconPath ?? App.ExecutablePath ?? string.Empty;

        // ===== Свойства для биндинга =====

        public string Name => App.Name;
        public string? Publisher => App.Publisher;
        public string? Version => App.Version;
        public string? ExecutablePath => App.ExecutablePath;
    }
}