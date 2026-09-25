using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundMeeter.ViewModels
{
    public partial class InstalledAppViewModel : ObservableObject
    {
        [ObservableProperty]
        private InstalledApp _app = new();

        [ObservableProperty]
        private ImageSource? _icon;

        public InstalledAppViewModel() { }

        public InstalledAppViewModel(InstalledApp app)
        {
            _app = app;
            _icon = LoadIcon();
        }

        private ImageSource? LoadIcon()
        {
            try
            {
                // 1. Если есть .ico файл — грузим напрямую
                var iconPath = App.IconPath;
                if (!string.IsNullOrEmpty(iconPath) &&
                    iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(iconPath))
                {
                    return LoadIcoFile(iconPath);
                }

                // 2. Если есть .exe/.dll — извлекаем иконку через Shell API
                var exePath = App.ExecutablePath ?? App.IconPath;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    return GetShellIcon(exePath);
                }

                return null;
            }
            catch
            {
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

        private static ImageSource? GetShellIcon(string filePath)
        {
            try
            {
                var shfi = new SHFILEINFO();
                var flags = SHGFI_ICON | SHGFI_LARGEICON;

                var result = SHGetFileInfo(
                    filePath,
                    0,
                    ref shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    flags);

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                    return null;

                // Конвертируем HICON в WPF ImageSource
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // Освобождаем нативный дескриптор иконки
                DestroyIcon(shfi.hIcon);

                imageSource.Freeze();
                return imageSource;
            }
            catch
            {
                return null;
            }
        }

        // ===== P/Invoke для Shell API =====

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
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ===== Свойства для биндинга =====

        public string Name => App.Name;
        public string? Publisher => App.Publisher;
        public string? Version => App.Version;
        public string? ExecutablePath => App.ExecutablePath;
    }
}
