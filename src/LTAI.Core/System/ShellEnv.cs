using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record ToolInfo
{
    public string Name { get; init; } = string.Empty;
    public bool Found { get; init; }
    public string? Path { get; init; }
    public string? Version { get; init; }
    public string? InstallHint { get; init; }
}

public sealed record ShellResult
{
    public string Command { get; init; } = string.Empty;
    public string Workdir { get; init; } = string.Empty;
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public long ElapsedMs { get; init; }
    public bool Truncated { get; init; }
    public bool Blocked { get; init; }
    public string? BlockReason { get; init; }
}

public sealed class ShellEnv
{
    private static readonly Lazy<ShellEnv> _instance = new(() => new ShellEnv(AutoLogger<ShellEnv>.Create()));
    public static ShellEnv Instance => _instance.Value;

    private readonly ILogger<ShellEnv> _logger;
    private readonly object _statsLock = new();
    private int _blockedCount;
    private int _runCount;
    private List<ToolInfo>? _cachedProbe;
    /// <summary>
    /// Delegate for external safety validation. Returns true if the command is safe.
    /// Set during DI startup to wire UnifiedSafetyGate without hard dependency.
    /// Signature: (toolName, input) => isSafe
    /// </summary>
    public Func<string, string, bool>? ExternalSafetyGate { get; set; }

    private static readonly string[] _dangerousPatterns = new[]
    {
        "rm -rf /",
        "rm -rf / --no-preserve-root",
        "dd if=/dev/zero",
        ":(){ :|:& };:",
        "mkfs.",
        "format c:",
        "format d:",
        "del /f /s /q",
        "rd /s /q c:",
        "diskpart",
        "reg delete",
        "schtasks /delete",
        "shutdown /s",
        "shutdown -h now",
        "halt",
        "reboot",
        "curl | sh",
        "wget -O - | sh",
        "eval",
        "exec("
    };

    private static readonly string[] _toolsToProbe = new[]
    {
        "python", "python3", "git", "node", "npm", "dotnet",
        "docker", "curl", "pwsh", "gh",
        "rg", "fd", "jq", "delta", "bat", "fzf"
    };

    public ShellEnv(ILogger<ShellEnv> logger)
    {
        _logger = logger;
    }

    public List<ToolInfo> ProbeEnvironment()
    {
        var results = new ConcurrentBag<ToolInfo>();

        Parallel.ForEach(_toolsToProbe, tool =>
        {
            var info = ProbeTool(tool);
            results.Add(info);
        });

        var list = results.OrderBy(t => t.Name).ToList();
        _cachedProbe = list;
        return list;
    }

    private ToolInfo ProbeTool(string toolName)
    {
        try
        {
            var platformTool = OperatingSystem.IsWindows() ? "where" : "which";

            if (MicroKernel.Default != null)
            {
                try
                {
                    var kResult = MicroKernel.Default?.ExecuteAsync(new KernelOp
                    {
                        Command = platformTool,
                        Arguments = toolName,
                        Timeout = TimeSpan.FromSeconds(5)
                    }).GetAwaiter().GetResult();

                    if (kResult?.Success == true && !string.IsNullOrEmpty(kResult.Data))
                    {
                        var kPath = kResult.Data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                        var kVersion = ProbeVersion(toolName);
                        return new ToolInfo
                        {
                            Name = toolName,
                            Found = true,
                            Path = kPath,
                            Version = kVersion,
                            InstallHint = null
                        };
                    }
                }
                catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = platformTool,
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return new ToolInfo { Name = toolName, Found = false, InstallHint = GetInstallHint(toolName) };
            }

            proc.WaitForExit(5000);
            var stdout = proc.StandardOutput.ReadToEnd().Trim();

            if (proc.ExitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                return new ToolInfo { Name = toolName, Found = false, InstallHint = GetInstallHint(toolName) };
            }

            var path = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            var version = ProbeVersion(toolName);

            return new ToolInfo
            {
                Name = toolName,
                Found = true,
                Path = path,
                Version = version,
                InstallHint = null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe tool {Tool}", toolName);
            return new ToolInfo { Name = toolName, Found = false, InstallHint = GetInstallHint(toolName) };
        }
    }

    private string? ProbeVersion(string toolName)
    {
        try
        {
            var versionArgs = toolName switch
            {
                "python" or "python3" => "--version",
                "git" => "--version",
                "node" => "--version",
                "npm" => "--version",
                "dotnet" => "--version",
                "docker" => "--version",
                "curl" => "--version",
                "pwsh" => "--version",
                "gh" => "--version",
                _ => "--version"
            };

            if (MicroKernel.Default != null)
            {
                try
                {
                    var kResult = MicroKernel.Default?.ExecuteAsync(new KernelOp
                    {
                        Command = toolName,
                        Arguments = versionArgs,
                        Timeout = TimeSpan.FromSeconds(5)
                    }).GetAwaiter().GetResult();

                    if (kResult?.Success == true)
                    {
                        var kOutput = (kResult.Data + kResult.Error).Trim();
                        if (!string.IsNullOrEmpty(kOutput))
                            return kOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    }
                }
                catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = toolName,
                Arguments = versionArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            var output = (proc?.StandardOutput.ReadToEnd() + proc?.StandardError.ReadToEnd()).Trim();
            return string.IsNullOrEmpty(output) ? null : output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetInstallHint(string toolName)
    {
        return toolName switch
        {
            "python" or "python3" => "https://python.org",
            "git" => "https://git-scm.com",
            "node" or "npm" => "https://nodejs.org",
            "dotnet" => "https://dotnet.microsoft.com",
            "docker" => "https://docker.com",
            "curl" => "winget install curl",
            "pwsh" => "winget install Microsoft.PowerShell",
            "gh" => "winget install GitHub.cli",
            "rg" => "winget install BurntSushi.ripgrep.GNU  # 10-100x faster than grep",
            "fd" => "winget install sharkdp.fd  # 10x faster than find",
            "jq" => "winget install jqlang.jq  # JSON processor",
            "delta" => "winget install dandavison.delta  # syntax-highlighting diff viewer",
            "bat" => "winget install sharkdp.bat  # cat with syntax highlighting",
            "fzf" => "winget install junegunn.fzf  # fuzzy finder",
            _ => null
        };
    }

    public string ProbeSummary()
    {
        var tools = _cachedProbe ?? ProbeEnvironment();
        var found = tools.Where(t => t.Found).ToList();
        var missing = tools.Where(t => !t.Found).ToList();

        var lines = new List<string>
        {
            "=== Shell Environment ===",
            $"Found ({found.Count}/{tools.Count}): {string.Join(", ", found.Select(t => t.Name))}",
        };

        if (missing.Count > 0)
        {
            lines.Add($"Missing: {string.Join(", ", missing.Select(t => t.Name))}");
        }

        return string.Join("\n", lines);
    }

    public string? IsDangerous(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var normalized = command.ToLowerInvariant().Replace(" ", "");
        foreach (var pattern in _dangerousPatterns)
        {
            var normalizedPattern = pattern.ToLowerInvariant().Replace(" ", "");
            if (normalized.Contains(normalizedPattern))
            {
                return $"Command blocked: matches dangerous pattern '{pattern}'";
            }
        }
        return null;
    }

    public async Task<ShellResult> Execute(string command, string workdir = ".", int timeoutSec = 30, int maxOutput = 50000)
    {
        // Layer 1: local dangerous pattern check
        var blockReason = IsDangerous(command);
        if (blockReason != null)
        {
            lock (_statsLock) { _blockedCount++; }
            _logger.LogWarning("Blocked dangerous command: {Reason}", blockReason);
            return new ShellResult
            {
                Command = command,
                Workdir = workdir,
                Blocked = true,
                BlockReason = blockReason,
                ExitCode = -1
            };
        }

        // Layer 2: External safety gate (wired to UnifiedSafetyGate at DI startup)
        if (ExternalSafetyGate != null && !ExternalSafetyGate("shell", command))
        {
            var safetyReason = "Blocked by external safety gate";
            lock (_statsLock) { _blockedCount++; }
            _logger.LogWarning("External safety gate blocked: {Command}", command);
            return new ShellResult
            {
                Command = command,
                Workdir = workdir,
                Blocked = true,
                BlockReason = safetyReason,
                ExitCode = -1
            };
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var fullWorkdir = Path.GetFullPath(AvoidTraversal(workdir));

            if (MicroKernel.Default != null)
            {
                try
                {
                    var shellExe = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
                    var shellArgs = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"";

                    var kResult = await MicroKernel.Default.ExecuteAsync(new KernelOp
                    {
                        Command = shellExe,
                        Arguments = shellArgs,
                        WorkingDirectory = fullWorkdir,
                        Timeout = TimeSpan.FromSeconds(timeoutSec)
                    }).ConfigureAwait(false);

                    var kStdout = kResult.Data ?? "";
                    var kStderr = kResult.Error ?? "";
                    var kTruncated = false;
                    if (kStdout.Length > maxOutput)
                    {
                        kStdout = kStdout[..maxOutput] + $"\n... [truncated at {maxOutput} chars]";
                        kTruncated = true;
                    }

                    lock (_statsLock) { _runCount++; }

                    return new ShellResult
                    {
                        Command = command,
                        Workdir = workdir,
                        Stdout = kStdout,
                        Stderr = kStderr,
                        ExitCode = kResult.Success ? 0 : 1,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Truncated = kTruncated
                    };
                }
                catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
                WorkingDirectory = fullWorkdir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var completed = proc.WaitForExit(timeoutSec * 1000);
            if (!completed)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* non-fatal */ }

                lock (_statsLock) { _runCount++; }
                return new ShellResult
                {
                    Command = command,
                    Workdir = workdir,
                    Stderr = "Process timed out",
                    ExitCode = -1,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            var truncated = false;
            if (stdout.Length > maxOutput)
            {
                stdout = stdout[..maxOutput] + $"\n... [truncated at {maxOutput} chars]";
                truncated = true;
            }

            lock (_statsLock) { _runCount++; }

            return new ShellResult
            {
                Command = command,
                Workdir = workdir,
                Stdout = stdout,
                Stderr = stderr,
                ExitCode = proc.ExitCode,
                ElapsedMs = sw.ElapsedMilliseconds,
                Truncated = truncated
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Shell execution failed: {Command}", command);
            lock (_statsLock) { _runCount++; }
            return new ShellResult
            {
                Command = command,
                Workdir = workdir,
                Stderr = ex.Message,
                ExitCode = -1,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    private static string AvoidTraversal(string workdir)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(workdir) ? "." : workdir);
        if (full.Contains(".."))
        {
            throw new InvalidOperationException($"Path traversal detected in workdir: {workdir}");
        }
        return full;
    }

    public async Task<ShellResult> ExecutePython(string code, string workdir = ".")
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_py_{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(tempFile, code).ConfigureAwait(false);
            return await Execute($"python \"{tempFile}\"", workdir);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* non-fatal */ }
        }
    }

    public async Task<ShellResult> ExecuteGit(string args, string workdir = ".")
    {
        return await Execute($"git {args}", workdir);
    }

    public (int Blocked, int Run, int ToolsFound) Stats()
    {
        int blocked, run;
        lock (_statsLock)
        {
            blocked = _blockedCount;
            run = _runCount;
        }
        var tools = _cachedProbe?.Count(t => t.Found) ?? 0;
        return (blocked, run, tools);
    }
}

file static class AutoLogger<T>
{
    public static ILogger<T> Create()
    {
        return NullLoggerFactory.Instance.CreateLogger<T>();
    }
}
