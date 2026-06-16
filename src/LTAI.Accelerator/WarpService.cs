using System.Diagnostics;

namespace LTAI.Accelerator;

public sealed class WarpService : IDisposable
{
    private string? _warpCliPath;
    private bool _available;

    public bool Available => _available;
    public bool Connected { get; private set; }
    public string Socks5Endpoint => "127.0.0.1:40000";

    /// <summary>Detect warp-cli on PATH or known locations. Safe to call anytime.</summary>
    public static string? FindWarpCli()
    {
        // Direct known paths (fastest — no directory scan)
        var known = new[]
        {
            @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe",
            @"C:\Program Files\Cloudflare WARP\warp-cli.exe",
            @"C:\Program Files (x86)\Cloudflare\Cloudflare WARP\warp-cli.exe",
        };
        foreach (var p in known)
            if (File.Exists(p)) return p;

        // Scan %ProgramFiles%/Cloudflare* for warp-cli.exe
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "Cloudflare*", SearchOption.TopDirectoryOnly))
                {
                    var cli = Path.Combine(dir, "Cloudflare WARP", "warp-cli.exe");
                    if (File.Exists(cli)) return cli;
                    cli = Path.Combine(dir, "warp-cli.exe");
                    if (File.Exists(cli)) return cli;
                }
            }
            catch
            {
                // non-critical, best-effort
            }
        }

        return null;
    }

    public WarpService()
    {
        _warpCliPath = FindWarpCli();
        _available = _warpCliPath != null;
    }

    public async Task<bool> ConnectAsync()
    {
        if (_warpCliPath == null) return false;

        try
        {
            KillWarpGui();

            RunWarp("set-mode proxy");
            await Task.Delay(500);
            RunWarp("connect");
            await Task.Delay(5000);

            // Poll status for "Connected" (up to 30s)
            for (int i = 0; i < 15; i++)
            {
                var status = RunWarp("status");
                if (status != null && status.Contains("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    Connected = true;
                    break;
                }
                await Task.Delay(2000);
            }

            // Fallback: if status never said Connected but SOCKS5 :40000 is reachable, trust it
            if (!Connected)
            {
                for (int i = 0; i < 5; i++)
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    try
                    {
                        await tcp.ConnectAsync("127.0.0.1", 40000);
                        Connected = true;
                        break;
                    }
                    catch { await Task.Delay(1000); }
                }
            }

            if (Connected)
            {
                _ = Task.Run(() =>
                {
                    try { WatchdogKillWarpGui(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WarpService watchdog error: {ex.Message}"); }
                });
                // Warm up SOCKS5 connection
                for (int i = 0; i < 5; i++)
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    try { await tcp.ConnectAsync("127.0.0.1", 40000); break; }
                    catch { await Task.Delay(1000); }
                }
            }

            return Connected;
        }
        catch
        {
            Connected = false;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_warpCliPath == null) return;
        try
        {
            RunWarp("disconnect");
            await Task.Delay(500);
        }
        catch
        {
            // non-critical, best-effort
        }
        Connected = false;
    }

    private string? RunWarp(string args)
    {
        if (_warpCliPath == null) return null;
        try
        {
            var psi = new ProcessStartInfo(_warpCliPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static void KillWarpGui()
    {
        try
        {
            // Only kill WARP GUI processes started by the current user, not system-wide
            var currentUser = Environment.UserName;
            foreach (var name in new[] { "Cloudflare WARP", "CloudflareWARP" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        // Only kill if process is from current user session
                        if (p.SessionId == Process.GetCurrentProcess().SessionId)
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                    }
                    catch
                    {
                        // non-critical, best-effort
                    }
                }
            }
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    private async Task WatchdogKillWarpGui()
    {
        try
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                KillWarpGui();
            }
        }
        catch (Exception)
        {
            // non-critical, best-effort
        }
    }

    public void Dispose()
    {
        if (Connected)
            _ = DisconnectAsync();
    }
}
