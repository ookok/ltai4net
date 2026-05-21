using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record Wsl2Result
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Distro { get; init; } = "";
    public string Output { get; init; } = "";
}

public sealed record WslDistroInfo
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public int WslVersion { get; init; }
}

public sealed class Wsl2Config
{
    public string Kernel { get; set; } = "";
    public int MemoryMb { get; set; }
    public int Processors { get; set; }
    public string SwapFile { get; set; } = "";
    public int SwapMb { get; set; }
    public bool LocalhostForwarding { get; set; } = true;

    public static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wslconfig");
}

public sealed class Wsl2Manager
{
    private readonly ILogger<Wsl2Manager> _logger;
    private readonly ShellEnv _shellEnv;

    public Wsl2Manager(ILogger<Wsl2Manager>? logger = null, ShellEnv? shellEnv = null)
    {
        _logger = logger ?? NullLogger<Wsl2Manager>.Instance;
        _shellEnv = shellEnv ?? ShellEnv.Instance;
    }

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public async Task<Wsl2Result> IsAvailable()
    {
        if (!IsWindows)
            return new Wsl2Result { Success = false, Message = "WSL2 only supported on Windows" };

        try
        {
            var result = await RunWslAsync("--status");
            return new Wsl2Result
            {
                Success = result.Success,
                Message = result.Success ? "WSL2 is available" : "WSL2 is not available",
                Output = result.Output
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check WSL availability");
            return new Wsl2Result { Success = false, Message = ex.Message };
        }
    }

    public async Task<Wsl2Result> ListDistros()
    {
        if (!IsWindows)
            return new Wsl2Result { Success = false, Message = "WSL2 only supported on Windows" };

        var availability = await IsAvailable();
        if (!availability.Success)
            return availability;

        try
        {
            var result = await RunWslAsync("--list --verbose");
            var distros = ParseDistroList(result.Output);

            var output = distros.Count == 0
                ? "No WSL distributions installed"
                : string.Join("\n", distros.Select(d =>
                    $"  {d.Name}  [State: {d.State}]  [WSL{d.WslVersion}]"));

            return new Wsl2Result
            {
                Success = true,
                Message = $"{distros.Count} distro(s) found",
                Output = output
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list WSL distros");
            return new Wsl2Result { Success = false, Message = ex.Message };
        }
    }

    public async Task<Wsl2Result> InstallDistro(string distroName)
    {
        if (!IsWindows)
            return new Wsl2Result { Success = false, Message = "WSL2 only supported on Windows" };

        var availability = await IsAvailable();
        if (!availability.Success)
            return availability;

        try
        {
            var result = await RunWslAsync($"--install -d {distroName}");
            return new Wsl2Result
            {
                Success = result.Success,
                Message = result.Success ? $"Distro '{distroName}' installed successfully" : $"Failed to install '{distroName}'",
                Distro = distroName,
                Output = result.Output
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install WSL distro {Distro}", distroName);
            return new Wsl2Result { Success = false, Message = ex.Message, Distro = distroName };
        }
    }

    public async Task<Wsl2Result> ExecuteInDistro(string distroName, string command)
    {
        if (!IsWindows)
            return new Wsl2Result { Success = false, Message = "WSL2 only supported on Windows" };

        var availability = await IsAvailable();
        if (!availability.Success)
            return availability;

        try
        {
            var result = await RunWslAsync($"-d {distroName} -- {command}");
            return new Wsl2Result
            {
                Success = result.Success,
                Message = result.Success ? "Command executed" : $"Command failed with exit code",
                Distro = distroName,
                Output = result.Output
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute command in WSL distro {Distro}", distroName);
            return new Wsl2Result { Success = false, Message = ex.Message, Distro = distroName };
        }
    }

    public async Task<Wsl2Result> SetResourceLimits(int memoryMb, int processors)
    {
        if (!IsWindows)
            return new Wsl2Result { Success = false, Message = "WSL2 only supported on Windows" };

        try
        {
            var config = ReadWslConfig();
            config.MemoryMb = memoryMb;
            config.Processors = processors;

            WriteWslConfig(config);
            await RunWslAsync("--shutdown");

            return new Wsl2Result
            {
                Success = true,
                Message = $"WSL2 resource limits set: {memoryMb} MB memory, {processors} processors. WSL restarted.",
                Output = ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set WSL resource limits");
            return new Wsl2Result { Success = false, Message = ex.Message };
        }
    }

    public Wsl2Config GetResourceLimits()
    {
        return ReadWslConfig();
    }

    private Wsl2Config ReadWslConfig()
    {
        var config = new Wsl2Config();

        try
        {
            if (!File.Exists(Wsl2Config.ConfigPath))
                return config;

            var content = File.ReadAllText(Wsl2Config.ConfigPath);
            var section = "";

            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    section = trimmed.Trim('[', ']');
                    continue;
                }

                if (section != "wsl2") continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;

                var key = trimmed[..eq].Trim().ToLowerInvariant();
                var value = trimmed[(eq + 1)..].Trim();

                switch (key)
                {
                    case "memory":
                        if (int.TryParse(Regex.Replace(value, @"[^\d]", ""), out var mem))
                            config.MemoryMb = mem / (value.Contains("GB", StringComparison.OrdinalIgnoreCase) ? 1 : 1024);
                        break;
                    case "processors":
                        if (int.TryParse(value, out var procs))
                            config.Processors = procs;
                        break;
                    case "kernel":
                        config.Kernel = value;
                        break;
                    case "swap":
                        if (int.TryParse(Regex.Replace(value, @"[^\d]", ""), out var swap))
                            config.SwapMb = swap / (value.Contains("GB", StringComparison.OrdinalIgnoreCase) ? 1 : 1024);
                        break;
                    case "swapfile":
                        config.SwapFile = value;
                        break;
                    case "localhostforwarding":
                        config.LocalhostForwarding = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read .wslconfig");
        }

        return config;
    }

    private void WriteWslConfig(Wsl2Config config)
    {
        var lines = new List<string>
        {
            "[wsl2]"
        };

        if (!string.IsNullOrEmpty(config.Kernel))
            lines.Add($"kernel={config.Kernel}");

        if (config.MemoryMb > 0)
            lines.Add($"memory={config.MemoryMb}MB");

        if (config.Processors > 0)
            lines.Add($"processors={config.Processors}");

        if (config.SwapMb > 0)
            lines.Add($"swap={config.SwapMb}MB");

        if (!string.IsNullOrEmpty(config.SwapFile))
            lines.Add($"swapFile={config.SwapFile}");

        lines.Add($"localhostForwarding={config.LocalhostForwarding.ToString().ToLowerInvariant()}");

        var dir = Path.GetDirectoryName(Wsl2Config.ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(Wsl2Config.ConfigPath, string.Join("\n", lines));
    }

    private static async Task<(bool Success, string Output)> RunWslAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "Failed to start wsl.exe");

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var combined = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return (proc.ExitCode == 0, combined);
        }
        catch (Exception)
        {
            return (false, "wsl.exe not found or not available");
        }
    }

    private static List<WslDistroInfo> ParseDistroList(string rawOutput)
    {
        var distros = new List<WslDistroInfo>();

        if (string.IsNullOrWhiteSpace(rawOutput))
            return distros;

        var lines = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            if (line.Contains("Windows Subsystem", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length < 2) continue;

            var name = parts[0].Trim();
            if (name.StartsWith("-") || name.StartsWith("*")) continue;

            var state = parts.Length > 1 ? parts[1].Trim() : "Unknown";
            var wslVersion = parts.Length > 2 && int.TryParse(parts[2].Trim(), out var v) ? v : 0;

            distros.Add(new WslDistroInfo
            {
                Name = name,
                State = state,
                WslVersion = wslVersion
            });
        }

        return distros;
    }
}
