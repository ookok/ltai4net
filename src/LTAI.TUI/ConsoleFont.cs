using System.Runtime.InteropServices;

namespace LTAI.TUI;

public static class ConsoleFont
{
    private const int StdOutputHandle = -11;
    private const uint FamilyMonospace = 0x36;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetCurrentConsoleFontEx(
        IntPtr consoleOutput,
        bool maximumWindow,
        ref ConsoleFontInfoEx consoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetCurrentConsoleFontEx(
        IntPtr consoleOutput,
        bool maximumWindow,
        ref ConsoleFontInfoEx consoleCurrentFontEx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConsoleFontInfoEx
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    public static void SetMapleMono(int fontSize = 14)
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

        var fontInfo = new ConsoleFontInfoEx
        {
            cbSize = (uint)Marshal.SizeOf<ConsoleFontInfoEx>(),
            FontFamily = FamilyMonospace,
            FaceName = "Maple Mono",
            dwFontSizeX = 0,
            dwFontSizeY = (short)fontSize,
            FontWeight = 400
        };

        if (!SetCurrentConsoleFontEx(handle, false, ref fontInfo))
        {
            // Fallback: try common monospace alternatives
            var fallbacks = new[] { "Maple Mono NF", "Cascadia Mono", "JetBrains Mono", "Fira Code", "Consolas" };
            foreach (var fb in fallbacks)
            {
                fontInfo.FaceName = fb;
                if (SetCurrentConsoleFontEx(handle, false, ref fontInfo))
                    break;
            }
        }
    }

    public static string GetCurrentFont()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return "unknown";

        var fontInfo = new ConsoleFontInfoEx { cbSize = (uint)Marshal.SizeOf<ConsoleFontInfoEx>() };
        if (GetCurrentConsoleFontEx(handle, false, ref fontInfo))
            return $"{fontInfo.FaceName} {fontInfo.dwFontSizeY}px";
        return "unknown";
    }
}
