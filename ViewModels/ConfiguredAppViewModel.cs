using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundMeeter.ViewModels
{
    public partial class ConfiguredAppViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _executablePath = string.Empty;

        [ObservableProperty]
        private ImageSource? _icon;

        /// <summary>Render-устройство, на которое правило перенаправляет приложение.</summary>
        public string DeviceId { get; }

        public ConfiguredAppViewModel(string name, string executablePath, string? iconPath, string? deviceId = null)
        {
            Name = name;
            ExecutablePath = executablePath;
            DeviceId = deviceId ?? string.Empty;
            _icon = LoadIcon(iconPath ?? executablePath);
        }

        private static ImageSource? LoadIcon(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                var shfi = new SHFILEINFO();
                var flags = SHGFI_ICON | SHGFI_LARGEICON;
                var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(shfi.hIcon);
                imageSource.Freeze();
                return imageSource;
            }
            catch { return null; }
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public IntPtr iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
