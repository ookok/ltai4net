using System.Collections;
using System.Diagnostics;
using LTAI.Core.Configuration;
using SecurityConfig = LTAI.Core.Configuration.ShellSecurityConfig;

namespace LTAI.Agent.Tools;

internal static class ShellSecurity
{
    public static HashSet<string> BlockedExes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> DangerousPatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string[] ProtectedPaths { get; set; } = [];
    public static HashSet<string> CodeExecNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> CodeExecArgs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string[] CommandSeps { get; set; } = [];

    /// <summary>Apply configuration (authoritative — replaces all previous values).</summary>
    public static void ApplyConfig(SecurityConfig config)
    {
        BlockedExes = new HashSet<string>(config.BlockedExes, StringComparer.OrdinalIgnoreCase);
        DangerousPatterns = new HashSet<string>(config.DangerousPatterns, StringComparer.OrdinalIgnoreCase);
        ProtectedPaths = config.ProtectedPaths;
        CodeExecNames = new HashSet<string>(config.CodeExecNames, StringComparer.OrdinalIgnoreCase);
        CodeExecArgs = new HashSet<string>(config.CodeExecArgs, StringComparer.OrdinalIgnoreCase);
        CommandSeps = config.CommandSeps;
    }

    internal static bool IsBlocked(string command)
    {
        var cmdLower = command.ToLowerInvariant();
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var executable = parts.Length > 0 ? parts[0].Trim() : "";
        var executableName = Path.GetFileName(executable.Trim('"').AsSpan()).ToString();

        if (BlockedExes.Contains(executableName))
            return true;

        if (DangerousPatterns.Any(p => cmdLower.Contains(p)))
            return true;

        if (parts.Length >= 2 && CodeExecNames.Contains(parts[0]) && CodeExecArgs.Contains(parts[1]))
            return true;

        if (CommandSeps.Any(sep => cmdLower.Contains(sep)))
        {
            var partsChk = command.Split(CommandSeps, StringSplitOptions.TrimEntries);
            foreach (var part in partsChk)
            {
                var partExec = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (partExec.Length == 0) continue;
                var pn = Path.GetFileName(partExec[0].AsSpan()).ToString();
                if (BlockedExes.Contains(pn) || CodeExecNames.Contains(pn))
                    return true;
            }
        }

        if (ProtectedPaths.Any(p => cmdLower.Contains(p.ToLowerInvariant())))
            return true;

        return false;
    }

    internal static string EscapeCmdArg(string arg)
    {
        return arg.Replace("\"", "\"\"");
    }

    internal static string EscapeBashArg(string arg)
    {
        return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    internal static void RestrictEnvironment(ProcessStartInfo psi, bool isWindows, string systemPathFallback)
    {
        psi.EnvironmentVariables["PATH"] = isWindows
            ? systemPathFallback
            : "/usr/bin:/bin:/usr/local/bin";
        psi.EnvironmentVariables.Remove("LD_PRELOAD");
        psi.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
        psi.EnvironmentVariables.Remove("DYLD_INSERT_LIBRARIES");
        psi.EnvironmentVariables.Remove("COR_ENABLE_PROFILING");
        psi.EnvironmentVariables.Remove("COR_PROFILER");
    }
}
