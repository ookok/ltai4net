using System.Diagnostics;

namespace LTAI.Accelerator;

public sealed class WarpService : IDisposable
{
    private string? _warpCliPath;
    private bool _available;

    public bool Available => _available;
    public bool Connected { get; private set; }
    public string Socks5Endpoint => "127.0.0.1:40000";

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
            RunWarp("set-mode proxy");
            await Task.Delay(500);
            RunWarp("connect");
            await Task.Delay(3000);

            var status = RunWarp("status");
            Connected = status != null && status.Contains("Connected", StringComparison.OrdinalIgnoreCase);

            if (Connected)
            {
                // Verify SOCKS5 proxy is actually listening
                for (int i = 0; i < 5; i++)
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    try
                    {
                        await tcp.ConnectAsync("127.0.0.1", 40000);
                        return true;
                    }
                    catch
                    {
                        await Task.Delay(1000);
                    }
                }
                Connected = false;
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
        catch { }
        Connected = false;
    }

    private string? FindWarpCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
            "warp-cli.exe",
            "warp-cli"
        };

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
                // Check PATH
                if (c == "warp-cli.exe" || c == "warp-cli")
                {
                    var which = OperatingSystem.IsWindows()
                        ? Process.Start(new ProcessStartInfo("where", c) { RedirectStandardOutput = true, UseShellExecute = false })
                        : Process.Start(new ProcessStartInfo("which", c) { RedirectStandardOutput = true, UseShellExecute = false });
                    if (which != null)
                    {
                        var output = which.StandardOutput.ReadToEnd().Trim();
                        which.WaitForExit(3000);
                        if (which.ExitCode == 0 && !string.IsNullOrEmpty(output))
                            return output.Split('\n')[0].Trim();
                    }
                }
            }
            catch { }
        }
        return null;
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

    public void Dispose()
    {
        if (Connected)
            _ = DisconnectAsync();
    }
}
