using System.Runtime.InteropServices;

namespace SoundMeeter.AudioPolicy
{
    [ComImport, Guid("AB3D4648-E242-459F-B02F-541C70306324"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioPolicyConfig
    {
        [PreserveSig] int GetIids(out int count, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int _00(); [PreserveSig] int _01(); [PreserveSig] int _02();
        [PreserveSig] int _03(); [PreserveSig] int _04(); [PreserveSig] int _05();
        [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08();
        [PreserveSig] int _09(); [PreserveSig] int _10(); [PreserveSig] int _11();
        [PreserveSig] int _12(); [PreserveSig] int _13(); [PreserveSig] int _14();
        [PreserveSig] int _15(); [PreserveSig] int _16(); [PreserveSig] int _17();
        [PreserveSig] int _18();

        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(
            uint processId, AudioFlow flow, ERole role, nint deviceIdHString);
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(
            uint processId, AudioFlow flow, ERole role, out nint deviceIdHString);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
