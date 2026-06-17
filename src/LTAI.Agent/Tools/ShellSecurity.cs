using System.Collections;
using System.Diagnostics;

namespace LTAI.Agent.Tools;

internal static class ShellSecurity
{
    internal static readonly HashSet<string> BlockedExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "chmod", "chown", "mkfs", "fdisk",
        "dd", "shutdown", "reboot", "init", "halt", "poweroff",
        "passwd", "useradd", "usermod", "groupadd", "fuser", "kill",
        "mount", "umount", "iptables", "ufw", "systemctl",
        "cmd", "cmd.exe", "certutil", "bitsadmin", "mshta", "cscript", "wmic",
        "reg", "schtasks", "diskpart", "bcdedit", "regsvr32", "rundll32",
        "attrib", "cacls", "takeown", "icacls", "vssadmin",
    };

    internal static readonly string[] DangerousPatterns =
    {
        "rm -rf /", "rm -rf ~", "rm -rf --no-preserve-root",
        ":(){ :|:& };:", "eval ", "exec ",
        "> /dev/", "dd if=", "wget -O - | sh", "curl .* | sh",
        "wget .* -O ", "certutil .* -urlcache", "bitsadmin .* /transfer",
    };

    internal static readonly HashSet<string> CodeExecNames = new(StringComparer.OrdinalIgnoreCase)
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

    internal static readonly HashSet<string> CodeExecArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "-command", "-e", "-i",
    };

    internal static readonly string[] CommandSeps = { " & ", " && ", " || ", " | ", "; " };

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

        foreach (var sep in CommandSeps)
        {
            if (cmdLower.Contains(sep))
            {
                var partsChk = command.Split(["&&", "||", "|", "&", ";"], StringSplitOptions.TrimEntries);
                foreach (var part in partsChk)
                {
                    var partExec = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (partExec.Length == 0) continue;
                    var pn = Path.GetFileName(partExec[0].AsSpan()).ToString();
                    if (BlockedExes.Contains(pn) || CodeExecNames.Contains(pn))
                        return true;
                }
                break;
            }
        }

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
