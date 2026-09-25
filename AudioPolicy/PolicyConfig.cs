using System.Runtime.InteropServices;

namespace SoundMeeter.AudioPolicy
{
    public static class PolicyConfig
    {
        public static int SetDefaultEndpoint(string deviceId, ERole role)
        {
            var client = (IPolicyConfig)new CPolicyConfigClient();
            try { return client.SetDefaultEndpoint(deviceId, role); }
            finally { Marshal.ReleaseComObject(client); }
        }

        public static int SetDefaultForAllRoles(string deviceId)
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                int hr = SetDefaultEndpoint(deviceId, role);
                if (hr != 0) return hr;
            }
            return 0;
        }
    }
}
