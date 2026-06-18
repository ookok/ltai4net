namespace LTAI.Core.Configuration;

/// <summary>
/// Shell execution security configuration.
/// All arrays are unioned with built-in defaults; user can add entries.
/// Configured in appsettings.json under LTAI:Security.
/// </summary>
public sealed class ShellSecurityConfig
{
    /// <summary>Commands blocked from execution entirely (e.g. sudo, dd, shutdown).</summary>
    public string[] BlockedExes { get; init; } = [];

    /// <summary>Dangerous shell patterns that are blocked (e.g. "rm -rf /").</summary>
    public string[] DangerousPatterns { get; init; } = [];

    /// <summary>Protected system paths never writable by agents.</summary>
    public string[] ProtectedPaths { get; init; } = [];

    /// <summary>Script engine names treated as code execution commands.</summary>
    public string[] CodeExecNames { get; init; } = [];

    /// <summary>Code execution argument patterns (e.g. "-c", "-e").</summary>
    public string[] CodeExecArgs { get; init; } = [];

    /// <summary>Command separator patterns for chaining detection.</summary>
    public string[] CommandSeps { get; init; } = [];

    /// <summary>Fallback PATH for shell execution on Windows.</summary>
    public string SystemPathFallback { get; init; } = @"C:\Windows\system32;C:\Windows";

    /// <summary>POSIX commands with Windows-specific warnings (command → explanation).</summary>
    public Dictionary<string, string> PlatformUnsupportedWarnings { get; init; } = new();

    /// <summary>PowerShell alias conflicts: name → .exe override (e.g. "ls" → "ls.exe").</summary>
    public Dictionary<string, string> PowerShellAliasConflicts { get; init; } = new();

    /// <summary>Max concurrent shell executions.</summary>
    public int ShellConcurrency { get; init; } = 8;
}
