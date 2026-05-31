using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static LTAI.Desktop.Terminal.NativeMethods;

namespace LTAI.Desktop.Terminal;

/// <summary>ConPTY 终端：管理伪控制台的生命周期和 I/O。</summary>
public sealed class VirtualTerminal : IDisposable
{
    private IntPtr _hPC;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private SafeFileHandle? _inputRead;
    private SafeFileHandle? _outputWrite; // kept to prevent GC of ConPTY write end
    private Process? _process;
    private Thread? _readerThread;
    private volatile bool _running;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly List<string> _screen = new();
    private readonly object _screenLock = new();
    private int _rows = 25, _cols = 80;

    public event Action? OutputUpdated;
    public IReadOnlyList<string> Screen { get { lock (_screenLock) return [.. _screen]; } }

    public void Start(string? shell = null, string? workingDir = null)
    {
        shell ??= Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash";
        workingDir ??= Environment.CurrentDirectory;

        CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0);
        CreatePipe(out _outputRead, out _outputWrite, IntPtr.Zero, 0);
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
        CreateProcess(null, shell, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDir, ref si, out var pi);
        _process = Process.GetProcessById(pi.dwProcessId);
        CloseHandle(pi.hThread);

        _running = true;
        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "ConPTY Reader" };
        _readerThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _process?.Kill(entireProcessTree: true);
        _process?.WaitForExit(5000);
        Dispose();
    }

    public void WriteInput(string text)
    {
        if (_inputWrite?.IsInvalid == false)
        {
            var bytes = Console.OutputEncoding.GetBytes(text);
            RandomAccess.Write(_inputWrite, bytes, 0);
        }
    }

    public void Resize(int cols, int rows)
    {
        _cols = cols; _rows = rows;
        if (_hPC != IntPtr.Zero)
            ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    }

    private void ReaderLoop()
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
                {
                    var clean = StripAnsi(lines[i]);
                    lock (_screenLock)
                    {
                        _screen.Add(clean);
                        if (_screen.Count > 1000) _screen.RemoveRange(0, _screen.Count - 500);
                    }
                }
                leftover = Console.OutputEncoding.GetBytes(lines[^1]);
                OutputUpdated?.Invoke();
            }
            catch { break; }
        }
    }

    private static string StripAnsi(string s)
    {
        // 移除 ANSI 转义序列 (CSI sequences)
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
        // 处理回车
        s = s.Replace("\r", "");
        return s;
    }

    public void Dispose()
    {
        _running = false;
        _inputWrite?.Dispose();
        _outputRead?.Dispose();
        _inputRead?.Dispose();
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        _process?.Dispose();
    }
}
