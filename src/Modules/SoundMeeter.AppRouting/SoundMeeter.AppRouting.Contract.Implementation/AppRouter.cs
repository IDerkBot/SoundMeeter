using SoundMeeter.AppRouting.Contract;
using System.Runtime.InteropServices;

namespace SoundMeeter.AppRouting.Contract.Implementation;

public sealed class AppRouter : IAppRouter
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private static readonly Guid IID = new("AB3D4648-E242-459F-B02F-541C70306324");
    private const string RenderIface = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    // Константа ProcessNoAudio переехала в контракт IAppRouter: её читает и вызывающий.

    [DllImport("combase.dll")]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, int length, out nint hstring);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")]
    static extern nint WindowsGetStringRawBuffer(nint hstring, out uint length);
    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    static IAudioPolicyConfig GetFactory()
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out nint classId));
        try
        {
            var iid = IID;
            int hr = RoGetActivationFactory(classId, ref iid, out nint factoryPtr);
            if (hr != 0) throw new COMException("RoGetActivationFactory failed", hr);
            var obj = Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            return (IAudioPolicyConfig)obj;
        }
        finally { WindowsDeleteString(classId); }
    }

    static string FormatEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return "";
        // Если это уже полный SWD-путь (\\?\SWD#MMDEVAPI#…#{iface}) — не оборачиваем повторно.
        if (endpointId.StartsWith(@"\\?\SWD#MMDEVAPI#", StringComparison.OrdinalIgnoreCase))
            return endpointId;
        return $@"\\?\SWD#MMDEVAPI#{endpointId}#{RenderIface}";
    }

    // Extracts the raw endpoint id back out of a formatted SWD route string.
    public static string ParseEndpoint(string route)
    {
        if (string.IsNullOrEmpty(route)) return "";
        const string marker = "MMDEVAPI#";
        int i = route.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int start = i + marker.Length;
        int end = route.IndexOf('#', start);
        return end < 0 ? route[start..] : route[start..end];
    }

    static string ReadHString(nint h)
    {
        if (h == 0) return "";
        nint buf = WindowsGetStringRawBuffer(h, out uint len);
        return len == 0 ? "" : Marshal.PtrToStringUni(buf, (int)len) ?? "";
    }

    // Returns the raw endpoint id the process is routed to, or "" if it follows default.
    public string GetRouteEndpoint(uint pid, AudioFlow flow = AudioFlow.Render)
    {
        var cfg = GetFactory();
        try
        {
            int hr = cfg.GetPersistedDefaultAudioEndpoint(pid, flow, ERole.Multimedia, out nint h);
            string route = hr == 0 ? ReadHString(h) : "";
            if (h != 0) WindowsDeleteString(h);
            return ParseEndpoint(route);
        }
        catch { return ""; }
        finally { Marshal.ReleaseComObject(cfg); }
    }

    // Routes a process to an endpoint. Empty endpointId resets it to the system default.
    public int SetRoute(uint pid, string endpointId, AudioFlow flow = AudioFlow.Render)
    {
        var cfg = GetFactory();
        try
        {
            string formatted = string.IsNullOrEmpty(endpointId) ? "" : FormatEndpoint(endpointId);
            int createHr = WindowsCreateString(formatted, formatted.Length, out nint h);
            if (createHr < 0) return createHr;
            try
            {
                int hr = cfg.SetPersistedDefaultAudioEndpoint(pid, flow, ERole.Multimedia, h);
                int hr2 = cfg.SetPersistedDefaultAudioEndpoint(pid, flow, ERole.Console, h);
                return hr != 0 ? hr : hr2;
            }
            finally { WindowsDeleteString(h); }
        }
        finally { Marshal.ReleaseComObject(cfg); }
    }
}
