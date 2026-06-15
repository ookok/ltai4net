using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LTAI.Desktop.Terminal;

/// <summary>
/// Cross-platform pseudo-terminal abstraction.
/// Windows: ConPTY API. Linux/macOS: forkpty from libc.
/// </summary>
public sealed class PseudoTerminal : IDisposable
{
    // ── Platform-specific state ──
    // Windows ConPTY handles
    private IntPtr _hPC;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private SafeFileHandle? _inputRead;
    private SafeFileHandle? _outputWrite;
    // Unix PTY
    private int _masterFd = -1;
    private int _childPid = -1;

    private Process? _process;
    private Thread? _readerThread;
    private volatile bool _running;
    private bool _disposed;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly List<string> _screen = new();
    private readonly object _screenLock = new();
    private int _rows = 25, _cols = 80;
    private bool _isUnix;

    public event Action? OutputUpdated;
    public IReadOnlyList<string> Screen { get { lock (_screenLock) return [.. _screen]; } }

    public PseudoTerminal()
    {
        _isUnix = !OperatingSystem.IsWindows();
    }

    public void Start(string? shell = null, string? workingDir = null)
    {
        workingDir ??= Environment.CurrentDirectory;

        if (_isUnix)
            StartUnix(shell ?? "/bin/bash", workingDir);
        else
            StartWindows(shell ?? "cmd.exe", workingDir);
    }

    public void Stop()
    {
        if (_disposed) return;
        _running = false;
        try
        {
            if (_isUnix)
            {
                if (_childPid > 0)
                {
                    kill(_childPid, 15); // SIGTERM
                    Thread.Sleep(200);
                    waitpid(_childPid, out _, 1); // WNOHANG
                    close(_masterFd);
                }
            }
            else
            {
                _process?.Kill(entireProcessTree: true);
                _process?.WaitForExit(5000);
            }
        }
        catch { }
        Dispose();
    }

    public void WriteInput(string text)
    {
        if (_isUnix)
        {
            if (_masterFd >= 0)
            {
                var bytes = Console.OutputEncoding.GetBytes(text);
                write(_masterFd, bytes, bytes.Length);
            }
        }
        else
        {
            if (_inputWrite?.IsInvalid == false)
            {
                var bytes = Console.OutputEncoding.GetBytes(text);
                RandomAccess.Write(_inputWrite, bytes, 0);
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        _cols = cols; _rows = rows;
        if (_isUnix)
        {
            if (_masterFd >= 0)
            {
                var ws = new winsize
                {
                    ws_row = (ushort)rows,
                    ws_col = (ushort)cols,
                    ws_xpixel = 0,
                    ws_ypixel = 0,
                };
                ioctl(_masterFd, TIOCSWINSZ, ref ws);
            }
        }
        else
        {
            if (_hPC != IntPtr.Zero)
                ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
        }
    }

    private void StartUnix(string shell, string workingDir)
    {
        var ws = new winsize
        {
            ws_row = (ushort)_rows,
            ws_col = (ushort)_cols,
            ws_xpixel = 0,
            ws_ypixel = 0,
        };

        int master;
        int pid = forkpty(out master, IntPtr.Zero, IntPtr.Zero, ref ws);
        if (pid < 0)
            throw new InvalidOperationException($"forkpty failed (errno={Marshal.GetLastPInvokeError()})");

        if (pid == 0)
        {
            // ── Child process ──
            // chdir to working directory
            if (!string.IsNullOrEmpty(workingDir))
                chdir(workingDir);

            // Build argument array: just the shell name (bash will start interactive)
            Marshal.FreeHGlobal(Marshal.StringToHGlobalAnsi(""));

            // Start shell
            execvp(shell, [shell]);

            // If exec fails, exit
            _exit(1);
        }

        // ── Parent process ──
        _masterFd = master;
        _childPid = pid;

        _running = true;
        _readerThread = new Thread(UnixReaderLoop) { IsBackground = true, Name = "UnixPTY Reader" };
        _readerThread.Start();
    }

    private void UnixReaderLoop()
    {
        var buf = new byte[4096];
        while (_running && _masterFd >= 0)
        {
            try
            {
                var n = read(_masterFd, buf, buf.Length);
                if (n <= 0) break;

                var text = Console.OutputEncoding.GetString(buf, 0, n);
                AppendOutput(text);
            }
            catch { break; }
        }
    }

    private void StartWindows(string shell, string workingDir)
    {
        WinCreatePipe(out var inputRead, out var inputWriteSafe, IntPtr.Zero, 0);
        WinCreatePipe(out var outputReadSafe, out var outputWrite, IntPtr.Zero, 0);
        _inputWrite = inputWriteSafe;
        _outputRead = outputReadSafe;
        _inputRead = inputRead;

        var size = new COORD { X = (short)_cols, Y = (short)_rows };
        var hr = CreatePseudoConsole(size, _inputRead, _outputWrite, 0, out _hPC);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: {hr}");

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        si.lpAttributeList = Marshal.AllocHGlobal(lpSize);
        InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref lpSize);
        var attr = (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE;
        UpdateProcThreadAttribute(si.lpAttributeList, 0, attr, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

        var flags = EXTENDED_STARTUPINFO_PRESENT;
        WinCreateProcess(null, shell, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDir, ref si, out var pi);
        _process = Process.GetProcessById(pi.dwProcessId);
        CloseHandle(pi.hThread);
        DeleteProcThreadAttributeList(si.lpAttributeList);
        Marshal.FreeHGlobal(si.lpAttributeList);

        _running = true;
        _readerThread = new Thread(WinReaderLoop) { IsBackground = true, Name = "ConPTY Reader" };
        _readerThread.Start();
    }

    private void WinReaderLoop()
    {
        var buf = new byte[4096];
        var leftover = Array.Empty<byte>();
        while (_running && _outputRead?.IsInvalid == false)
        {
            try
            {
                var read = RandomAccess.Read(_outputRead, buf, 0);
                if (read == 0) break;
                leftover = [.. leftover, .. buf[..read]];
                var text = Console.OutputEncoding.GetString(leftover);
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length - 1; i++)
                    AppendOutput(StripAnsi(lines[i]));
                leftover = Console.OutputEncoding.GetBytes(lines[^1]);
                OutputUpdated?.Invoke();
            }
            catch { break; }
        }
    }

    private void AppendOutput(string text)
    {
        lock (_screenLock)
        {
            _screen.Add(text);
            if (_screen.Count > 1000) _screen.RemoveRange(0, _screen.Count - 500);
        }
        OutputUpdated?.Invoke();
    }

    private static string StripAnsi(string s)
    {
        int idx;
        while ((idx = s.IndexOf('\x1b')) >= 0)
        {
            var end = idx + 1;
            if (end < s.Length && s[end] == '[')
            {
                end++;
                while (end < s.Length && !(s[end] >= 0x40 && s[end] <= 0x7E)) end++;
                if (end < s.Length) end++;
                s = s[..idx] + s[end..];
            }
            else
                s = s[..idx] + s[(idx + 1)..];
        }
        return s.Replace("\r", "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        if (_isUnix)
        {
            if (_masterFd >= 0) { close(_masterFd); _masterFd = -1; }
        }
        else
        {
            _inputWrite?.Dispose();
            _outputRead?.Dispose();
            _inputRead?.Dispose();
            _outputWrite?.Dispose();
            if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
            _process?.Dispose();
        }
    }

    // ── Unix P/Invoke ──────────────────────────────────────────────────

    // TIOCSWINSZ differs between Linux (0x5414) and macOS (0x80087467).
    // Set once at class init since it won't change at runtime.
    private static readonly ulong TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;

    [StructLayout(LayoutKind.Sequential)]
    private struct winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int forkpty(out int amaster, IntPtr name, IntPtr termios, ref winsize winSize);

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string[] argv);

    [DllImport("libc", SetLastError = true)]
    private static extern void _exit(int status);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref winsize ws);

    [DllImport("libc", SetLastError = true)]
    private static extern int chdir(string path);

    // ── Windows P/Invoke ──────────────────────────────────────────────

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop;
        public string? lpTitle; public int dwX; public int dwY;
        public int dwXSize; public int dwYSize; public int dwXCountChars;
        public int dwYCountChars; public int dwFillAttribute; public int dwFlags;
        public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
        public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess; public IntPtr hThread;
        public int dwProcessId; public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    private static void WinCreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size)
    {
        if (!CreatePipe(out read, out write, attrs, size))
            throw new InvalidOperationException("CreatePipe failed");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    private static void WinCreateProcess(string? app, string cmd, IntPtr procAttr, IntPtr threadAttr, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi)
    {
        if (!CreateProcess(app, cmd, procAttr, threadAttr, inherit, flags, env, dir, ref si, out pi))
            throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastPInvokeError()}");
    }
}
