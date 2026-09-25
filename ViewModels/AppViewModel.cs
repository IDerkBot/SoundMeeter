using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundMeeter.ViewModels
{
    public partial class AppViewModel : ObservableObject
    {
        [ObservableProperty]
        private RunningApp _app = new();

        [ObservableProperty]
        private ImageSource? _icon;

        public AppViewModel() { }

        public AppViewModel(RunningApp app, IAudioService audioService)
        {
            _app = app;
            _icon = LoadIcon(app.ProcessId);
        }

        public string? ExecutablePath => App.ExecutablePath;

        private ImageSource? LoadIcon(uint processId)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById((int)processId);
                var fileName = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(fileName)) return null;

                return GetShellIcon(fileName);
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
                    filePath, 0, ref shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                    return null;

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

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

        public string Name => App.Name;
        public uint ProcessId => App.ProcessId;
        public string CurrentDeviceId => App.CurrentDeviceId;
    }
}
