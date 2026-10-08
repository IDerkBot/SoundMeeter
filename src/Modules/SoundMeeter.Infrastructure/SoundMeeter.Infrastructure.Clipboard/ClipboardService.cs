using System.Windows;
using System.Windows.Threading;

namespace SoundMeeter.Services;

/// <summary>
/// Реализация <see cref="IClipboardService"/> поверх WPF (SM-A10).
///
/// Копирование буфера должно идти с UI-потока: WPF-буфер живёт в STA и требует
/// <c>OleInitialize</c>, а ещё может быть занят другим процессом — тогда
/// <see cref="Clipboard.SetText(string)"/> бросает <see cref="System.Runtime.InteropServices.COMException"/>.
/// Ошибку не глотаем: вызывающий ViewModel показывает пользователю, что
/// скопировать не удалось (раньше он ловил то же самое, только вокруг
/// <c>Clipboard</c> прямо в ядре).
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException(
                "Буфер обмена доступен только с UI-потока: вызывайте из команды ViewModel.");

        Clipboard.SetText(text);
    }
}