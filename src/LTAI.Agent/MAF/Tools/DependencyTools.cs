using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("Windows package manager tools for automatic dependency installation and environment self-healing")]
public sealed class DependencyTools
{
    private static readonly HashSet<string> ChocoKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "visualstudio", "dotnet-sdk", "dotnet-runtime", "msbuild", "sql-server",
        "vcredist", "netfx", "javaruntime", "docker-desktop", "nvm"
    };

    private static readonly HashSet<string> ScoopKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "nodejs", "python", "ffmpeg", "curl", "ripgrep", "fd",
        "jq", "imagemagick", "aria2", "upx", "youtube-dl", "pandoc",
        "make", "gcc", "cmake", "openssl", "7zip", "neovim", "lazygit"
    };

    [Description("Check if a CLI tool is available on the system PATH. Returns path and version if found.")]
    public static async Task<string> CheckTool(
        [Description("CLI tool name, e.g. 'git', 'node', 'python', 'ffmpeg'")] string toolName,
        CancellationToken cancellationToken = default)
    {
        var (found, path, version) = await FindToolAsync(toolName, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            tool = toolName,
            available = found,
            path = path ?? "not found",
            version = version ?? "unknown"
        });
    }

    [Description("Install a CLI tool using the best available package manager (Scoop or Chocolatey). Auto-detects which manager to use based on tool type.")]
    public static async Task<string> InstallTool(
        [Description("Tool name to install, e.g. 'git', 'nodejs', 'python', 'ffmpeg'")] string toolName,
        [Description("Force a specific package manager: 'scoop', 'choco', or 'auto' to let the system decide")] string manager = "auto",
        CancellationToken cancellationToken = default)
    {
        var (found, _, _) = await FindToolAsync(toolName, cancellationToken);
        if (found)
            return JsonSerializer.Serialize(new { tool = toolName, status = "already_installed", message = "Tool is already available on PATH" });

        var selectedManager = manager.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SelectManager(toolName)
            : manager.ToLowerInvariant();

        var (installOk, installOutput) = await RunInstallerAsync(selectedManager, toolName, cancellationToken);
        if (!installOk)
        {
            if (selectedManager == "scoop" && manager.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                (installOk, installOutput) = await RunInstallerAsync("choco", toolName, cancellationToken);
                selectedManager = "choco (fallback)";
            }
            else if (selectedManager == "choco" && manager.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                (installOk, installOutput) = await RunInstallerAsync("scoop", toolName, cancellationToken);
                selectedManager = "scoop (fallback)";
            }
        }

        return JsonSerializer.Serialize(new
        {
            tool = toolName,
            manager = selectedManager,
            success = installOk,
            output = Truncate(installOutput, 500)
        });
    }

    [Description("Check and install all common development tools at once (git, nodejs, python, ffmpeg, curl).")]
    public static async Task<string> InstallDevSuite(CancellationToken ct = default)
    {
        var tools = new[] { "git", "nodejs", "python", "ffmpeg", "curl" };
        var results = new List<object>();

        foreach (var tool in tools)
        {
            var (found, _, ver) = await FindToolAsync(tool, ct);
            if (found)
            {
                results.Add(new { tool, status = "ok", version = ver });
            }
            else
            {
                var manager = SelectManager(tool);
                var (ok, output) = await RunInstallerAsync(manager, tool, ct);
                results.Add(new { tool, status = ok ? "installed" : "failed", manager, output = Truncate(output, 200) });
            }
        }

        var succeeded = results.Count(r => r.ToString()!.Contains("\"ok\"") || r.ToString()!.Contains("\"installed\""));
        return JsonSerializer.Serialize(new { total = tools.Length, succeeded, failed = tools.Length - succeeded, results });
    }

    [Description("Check if Chocolatey and/or Scoop are installed and working.")]
    public static async Task<string> CheckPackageManagers(CancellationToken ct = default)
    {
        var chocoOk = await CheckCommandAsync("choco --version", ct);
        var scoopOk = await CheckCommandAsync("scoop --version", ct);

        return JsonSerializer.Serialize(new
        {
            chocolatey = new { installed = chocoOk.success, version = chocoOk.output.Trim() },
            scoop = new { installed = scoopOk.success, version = scoopOk.output.Trim() },
            recommended = GetInstallInstructions(!chocoOk.success, !scoopOk.success)
        });
    }

    private static async Task<(bool found, string? path, string? version)> FindToolAsync(string name, CancellationToken ct)
    {
        var toolExe = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var whereResult = await CheckCommandAsync($"where {toolExe} 2>nul", ct);
        if (!whereResult.success) return (false, null, null);

        var path = whereResult.output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return (false, null, null);

        var verResult = await CheckCommandAsync(GetVersionCommand(name), ct);
        return (true, path, verResult.success ? verResult.output.Trim() : null);
    }

    private static string GetVersionCommand(string tool) => tool.ToLowerInvariant() switch
    {
        "git" => "git --version",
        "node" or "nodejs" => "node --version",
        "python" => "python --version",
        "ffmpeg" => "ffmpeg -version",
        "curl" => "curl --version",
        "ripgrep" or "rg" => "rg --version",
        "fd" => "fd --version",
        "7zip" or "7z" => "7z --help",
        _ => $"{tool} --version"
    };

    private static string SelectManager(string tool) =>
        ChocoKeywords.Any(k => tool.Contains(k, StringComparison.OrdinalIgnoreCase)) ? "choco" : "scoop";

    private static async Task<(bool success, string output)> RunInstallerAsync(string manager, string tool, CancellationToken ct)
    {
        var cmd = manager switch
        {
            "scoop" => $"scoop install {tool}",
            "choco" => $"choco install {tool} -y",
            _ => throw new ArgumentException($"Unknown package manager: {manager}")
        };
        return await CheckCommandAsync(cmd, ct);
    }

    private static async Task<(bool success, string output)> CheckCommandAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return (false, "Process start failed");

            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (p.ExitCode == 0, output);
        }
        catch
        {
            return (false, "Command execution failed");
        }
    }

    private static string GetInstallInstructions(bool needChoco, bool needScoop)
    {
        var parts = new List<string>();
        if (needScoop) parts.Add("Set-ExecutionPolicy RemoteSigned -Scope CurrentUser; irm get.scoop.sh | iex");
        if (needChoco) parts.Add(@"Set-ExecutionPolicy Bypass -Scope Process; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))");
        return string.Join(" | ", parts);
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;
}
