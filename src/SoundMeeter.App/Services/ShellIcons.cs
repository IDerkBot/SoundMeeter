using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundMeeter.Services;

/// <summary>
/// Значки приложений для UI (SM-A10). Раньше извлечение значка из оболочки было
/// написано трижды — в <c>AppViewModel</c>, <c>ConfiguredAppViewModel</c> и
/// <c>InstalledAppViewModel</c>, — и в четвёртый раз в мёртвом
/// <c>IAudioService.GetAppIcon</c>. Теперь это единственная копия, а ядро про
/// значки не знает вообще: конвертеры берут значок по пути или по PID.
///
/// Кэш нужен по двум причинам: список установленных программ пересобирается на
/// каждый символ в строке поиска (иначе на каждый символ читался бы реестр и
/// дёргалась оболочка), а <c>BitmapSource</c> здесь всегда <c>Freeze</c>'нут, то
/// безопасен для показа с любого потока.
/// </summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> FileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значок файла: <c>.ico</c> читаем как есть, остальное — через оболочку.</summary>
    public static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        lock (FileCache)
        {
            if (FileCache.TryGetValue(path, out var cached)) return cached;

            var icon = File.Exists(path)
                ? path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                    ? LoadIcoFile(path)
                    : FromShell(path)
                : null;

            FileCache[path] = icon;
            return icon;
        }
    }

    /// <summary>Значок запущенного процесса — по имени его модуля.</summary>
    public static ImageSource? FromProcess(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return FromFile(process.MainModule?.FileName);
        }
        catch
        {
            // Процесс мог завершиться или быть недоступен: значок не обязателен.
            return null;
        }
    }

    private static BitmapImage? LoadIcoFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? FromShell(string filePath)
    {
        try
        {
            var shfi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON;

            var result = SHGetFileInfo(
                filePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            // Конвертируем HICON в WPF ImageSource.
            var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Освобождаем нативный дескриптор иконки.
            DestroyIcon(shfi.hIcon);

            imageSource.Freeze();
            return imageSource;
        }
        catch
        {
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public IntPtr iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}