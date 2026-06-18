using System.Collections;
using System.Diagnostics;
using LTAI.Core.Configuration;
using SecurityConfig = LTAI.Core.Configuration.ShellSecurityConfig;

namespace LTAI.Agent.Tools;

internal static class ShellSecurity
{
    static ShellSecurity()
    {
        ResetToDefaults();
    }

    public static void ResetToDefaults()
    {
        BlockedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sudo", "su", "chmod", "chown", "mkfs", "fdisk",
            "dd", "shutdown", "reboot", "init", "halt", "poweroff",
            "passwd", "useradd", "usermod", "groupadd", "fuser", "kill",
            "mount", "umount", "iptables", "ufw", "systemctl",
            "cmd", "cmd.exe", "certutil", "bitsadmin", "mshta", "cscript", "wmic",
            "reg", "schtasks", "diskpart", "bcdedit", "regsvr32", "rundll32",
            "attrib", "cacls", "takeown", "icacls", "vssadmin",
        };
        DangerousPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rm -rf /", "rm -rf ~", "rm -rf --no-preserve-root",
            ":(){ :|:& };:", "eval ", "exec ",
            "> /dev/", "dd if=", "wget -O - | sh", "curl .* | sh",
            "wget .* -O ", "certutil .* -urlcache", "bitsadmin .* /transfer",
        };
        ProtectedPaths =
        [
            "/etc", "/sys", "/proc", "/dev", "/boot",
            "/var/log", "/var/lib", "/var/spool",
            "C:\\Windows", "C:\\Windows\\System32",
            "C:\\Program Files", "C:\\Program Files (x86)",
            "C:\\ProgramData",
        ];
        CodeExecNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "sh", "zsh", "dash", "ksh", "fish",
            "python", "python2", "python3", "py",
            "perl", "perl5",
            "ruby", "rake",
            "php",
            "lua", "luajit",
            "tclsh", "wish",
            "powershell", "pwsh", "powershell.exe", "pwsh.exe",
        };
        CodeExecArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-c", "-command", "-e", "-i",
        };
        CommandSeps = ["&&", "||", "|", "&", ";"];
    }

    public static HashSet<string> BlockedExes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> DangerousPatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string[] ProtectedPaths { get; set; } = [];
    public static HashSet<string> CodeExecNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> CodeExecArgs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string[] CommandSeps { get; set; } = [];

    /// <summary>Apply user config on top of built-in defaults.</summary>
    public static void ApplyConfig(SecurityConfig config)
    {
        if (config.BlockedExes.Length > 0)
        {
            foreach (var exe in config.BlockedExes)
                BlockedExes.Add(exe);
        }
        if (config.DangerousPatterns.Length > 0)
        {
            foreach (var p in config.DangerousPatterns)
                DangerousPatterns.Add(p);
        }
        if (config.ProtectedPaths.Length > 0)
            ProtectedPaths = [.. ProtectedPaths, .. config.ProtectedPaths];
        if (config.CodeExecNames.Length > 0)
        {
            foreach (var n in config.CodeExecNames)
                CodeExecNames.Add(n);
        }
        if (config.CodeExecArgs.Length > 0)
        {
            foreach (var a in config.CodeExecArgs)
                CodeExecArgs.Add(a);
        }
        if (config.CommandSeps.Length > 0)
            CommandSeps = [.. CommandSeps, .. config.CommandSeps];
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
