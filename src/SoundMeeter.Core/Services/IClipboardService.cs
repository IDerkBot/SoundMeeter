namespace SoundMeeter.Services
{
    /// <summary>
    /// Буфер обмена (SM-A10). Ядро копирует в него диагностику и адрес дока OBS,
    /// а <c>System.Windows.Clipboard</c> — это WPF, который в ядре быть не должен.
    ///
    /// Особенность реализации, о которой стоит помнить: <c>Clipboard</c> в WPF
    /// работает только из STA-потока с открытым <c>OleInitialize</c>, то есть из
    /// UI-потока приложения, и может бросить <see cref="System.Runtime.InteropServices.COMException"/>,
    /// если буфер занят другим процессом. Поэтому метод не «проглатывает»
    /// ошибку, а пробрасывает её вызывающему — ViewModel покажет пользователю
    /// «не удалось скопировать» с текстом ошибки, как и раньше.
    /// </summary>
    public interface IClipboardService
    {
        /// <summary>Положить текст в буфер обмена.</summary>
        void SetText(string text);
    }
}