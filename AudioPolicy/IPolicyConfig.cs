using System.Runtime.InteropServices;

namespace SoundMeeter.AudioPolicy
{
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint p0, nint p1);
        [PreserveSig] int GetDeviceFormat(nint p0, int p1, nint p2);
        [PreserveSig] int ResetDeviceFormat(nint p0);
        [PreserveSig] int SetDeviceFormat(nint p0, nint p1, nint p2);
        [PreserveSig] int GetProcessingPeriod(nint p0, int p1, nint p2, nint p3);
        [PreserveSig] int SetProcessingPeriod(nint p0, nint p1);
        [PreserveSig] int GetShareMode(nint p0, nint p1);
        [PreserveSig] int SetShareMode(nint p0, nint p1);
        [PreserveSig] int GetPropertyValue(nint p0, int p1, nint p2, nint p3);
        [PreserveSig] int SetPropertyValue(nint p0, int p1, nint p2, nint p3);
        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(nint p0, int p1);
    }
}
