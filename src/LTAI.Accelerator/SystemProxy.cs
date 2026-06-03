using System.Runtime.InteropServices;

namespace LTAI.Accelerator;

public static class SystemProxy
{
    private const int INTERNET_OPTION_PROXY = 38;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct INTERNET_PROXY_INFO
    {
        public int dwAccessType;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpszProxy;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpszProxyBypass;
    }

    private const int INTERNET_OPEN_TYPE_DIRECT = 1;
    private const int INTERNET_OPEN_TYPE_PROXY = 3;

    public static void Enable(string proxyAddress)
    {
        var info = new INTERNET_PROXY_INFO
        {
            dwAccessType = INTERNET_OPEN_TYPE_PROXY,
            lpszProxy = proxyAddress,
            lpszProxyBypass = "10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;127.0.0.1;localhost"
        };

        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            InternetSetOption(nint.Zero, INTERNET_OPTION_PROXY, ptr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        // Notify WinINET that settings changed
        InternetSetOption(nint.Zero, INTERNET_OPTION_SETTINGS_CHANGED, nint.Zero, 0);
        InternetSetOption(nint.Zero, INTERNET_OPTION_REFRESH, nint.Zero, 0);
    }

    public static void Disable()
    {
        var info = new INTERNET_PROXY_INFO
        {
            dwAccessType = INTERNET_OPEN_TYPE_DIRECT,
            lpszProxy = null!,
            lpszProxyBypass = null!
        };

        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            InternetSetOption(nint.Zero, INTERNET_OPTION_PROXY, ptr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        InternetSetOption(nint.Zero, INTERNET_OPTION_SETTINGS_CHANGED, nint.Zero, 0);
        InternetSetOption(nint.Zero, INTERNET_OPTION_REFRESH, nint.Zero, 0);
    }
}
