using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundMeeter.ViewModels
{
    /// <summary>
    /// Постоянное правило роутинга: приложение, которое всегда играет в
    /// заданное устройство (то, что пользователь перетащил на стрип и сохранил).
    ///
    /// Значок рисует UI по <see cref="IconPath"/> (см. замечание про значки в
    /// <see cref="AppViewModel"/>), поэтому ядру достаточно знать путь.
    /// </summary>
    public partial class ConfiguredAppViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _executablePath = string.Empty;

        /// <summary>Render-устройство, на которое правило перенаправляет приложение.</summary>
        public string DeviceId { get; }

        /// <summary>
        /// Откуда UI возьмёт значок: путь из реестра (<c>DisplayIcon</c>), а если
        /// его нет — сам исполняемый файл. Раньше эта строка уходила только в
        /// <c>SHGetFileInfo</c> внутри ViewModel, и путь наружу не отдавался.
        /// </summary>
        public string IconPath { get; }

        public ConfiguredAppViewModel(string name, string executablePath, string? iconPath, string? deviceId = null)
        {
            Name = name;
            ExecutablePath = executablePath;
            DeviceId = deviceId ?? string.Empty;
            IconPath = iconPath ?? executablePath;
        }
    }
}