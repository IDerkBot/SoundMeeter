using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels
{
    /// <summary>
    /// Запущенное приложение, играющее звук.
    ///
    /// Значок приложения в UI рисует не ViewModel, а конвертер по
    /// <see cref="ProcessId"/> (<c>ProcessIconConverter</c> в приложении):
    /// извлечение иконки из HICON — это WPF (<c>BitmapSource</c>), и в ядре ему
    /// не место. Раньше здесь стоял свой P/Invoke в оболочку <c>SHGetFileInfo</c>
    /// и свойство типа <c>ImageSource</c>; копия этого кода была ещё в двух
    /// ViewModel'ах, а четвёртая (метод <c>GetAppIcon</c> у <c>IAudioService</c>)
    /// вообще ничего не вызывала.
    /// </summary>
    public partial class AppViewModel : ObservableObject
    {
        [ObservableProperty]
        private RunningApp _app = new();

        public AppViewModel() { }

        public AppViewModel(RunningApp app)
        {
            _app = app;
        }

        public string? ExecutablePath => App.ExecutablePath;

        public string Name => App.Name;
        public uint ProcessId => App.ProcessId;
        public string CurrentDeviceId => App.CurrentDeviceId;
    }
}