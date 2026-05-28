using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Structural shell command validator — replaces the old substring-based DangerousCommands blacklist.
/// Three-layer defense:
///   1. Dangerous pattern regex (category-based)
///   2. Command injection structural detection
///   3. Process/disk destructive operation detection
/// </summary>
public static class ShellCommandValidator
{
    // ── Layer 1: Known dangerous command patterns (structured) ──

    private static readonly (string Name, Regex Pattern)[] DangerousPatterns =
    {
        // Destructive filesystem: delete entire tree
        ("recursive_delete_root", new Regex(
            @"^\s*(?:sudo\s+)?(?:rm|del|rd)\s+(?:-rf|/f\s*/s|/q\s*/s)?\s*(?:[/\\]\s*[*?]|[/\\]\s*\.\s*$|[/\\]\s*$|C:\s*[/\\])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Disk destroy: dd, mkfs, format
        ("disk_destroy", new Regex(
            @"^\s*(?:sudo\s+)?(?:dd\s+(?:if\s*=\s*)?/dev/|mkfs\s*\.|format\s+|fdisk\s+|parted\s+|mkswap\s+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Direct block device write
        ("block_device_write", new Regex(
            @"[>|]\s*/dev/(?:sd[a-z]|nvme\d|hd[a-z]|sda\d|xvd[a-z]|mmcblk)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Shutdown / reboot / halt
        ("system_shutdown", new Regex(
            @"^\s*(?:sudo\s+)?(?:shutdown|poweroff|halt|reboot|init\s+0|init\s+6)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Fork bomb
        ("fork_bomb", new Regex(
            @":\(\)\s*\{|:\|:|fork\s+bomb|while\s+true\s*;?\s*do\s+(?:fork|:)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Chmod / chown on system root
        ("permission_escalation", new Regex(
            @"^\s*(?:sudo\s+)?(?:chmod\s+(?:-R\s+)?777\s+/|chown\s+(?:-R\s+)?\w+\s+/)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // User management (unauthorized)
        ("user_management", new Regex(
            @"^\s*(?:sudo\s+)?(?:useradd|userdel|usermod|passwd|adduser|deluser)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    // ── Layer 2: Remote code execution patterns ──

    private static readonly (string Name, Regex Pattern)[] RemoteExecutionPatterns =
    {
        ("curl_pipe_shell", new Regex(
            @"(?:curl|wget)\s+(?:-\s*[a-zA-Z]*[oO]\s+[^-]|-\s*[a-zA-Z]*[sS]\s*)?.*?[\|]\s*(?:sh|bash|zsh|dash|fish)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("wget_exec", new Regex(
            @"wget\s+.*?(?:-O\s*-|--output-document\s*=\s*-)\s*[\|]\s*(?:sh|bash)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("python_code_exec", new Regex(
            @"(?:python|perl|ruby|php)\s+(?:-c\s+['""]|-(?:e|E)\s+['""])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("netcat_reverse_shell", new Regex(
            @"nc\s+(?:-e\s+|[\w.]+:?\d+\s+-e\s+)|ncat\s+|socat\s+|mkfifo\s+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("base64_decode_exec", new Regex(
            @"(?:base64|echo.*\|.*base64)\s*-d\s*[\|]\s*(?:sh|bash)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    // ── Layer 3: Command injection structural patterns ──

    private static readonly Regex[] InjectionPatterns =
    {
        // Subshell with $(...)
        new(@"\$\(.*?\)", RegexOptions.Compiled),

        // Backtick subshell
        new(@"`[^`]+`", RegexOptions.Compiled),

        // Pipe to shell (already in RemoteExecutionPatterns, catch variants here)
        new(@"\|+\s*(?:sh|bash|zsh|dash|fish|perl|python|ruby|php)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Unsafe eval-style execution
        new(@"\beval\s+\$?[`(]|\beval\s+""|\beval\s+'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    // ── Safe base commands (allowlist foundation) ──

    private static readonly HashSet<string> UnsafeCommandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "passwd", "chsh", "chfn", "mount", "umount",
        "modprobe", "insmod", "rmmod", "dpkg", "rpm", "apt-get",
        "apt", "pacman", "yum", "dnf", "zypper", "snap",
        "systemctl", "service", "initctl", "journalctl",
        "crontab", "at", "batch", "kill", "pkill", "killall",
        "renice", "nice", "ulimit", "prctl",
    };

    /// <summary>
    /// Validate a shell command against all three defense layers.
    /// Checks the full command AND each line individually to prevent \n-based bypass.
    /// </summary>
    /// <returns>A tuple of (Allowed, Reason). If not allowed, Reason explains why.</returns>
    public static (bool Allowed, string Reason) Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (true, "");

        var trimmed = command.Trim();

        // Anti-bypass: validate each line individually (prevents \n/bin/sh bypass)
        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var lineTrimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(lineTrimmed)) continue;

            var lineResult = ValidateLine(lineTrimmed);
            if (!lineResult.Allowed)
                return lineResult;
        }

        // Also validate the full joined command (catches multi-line constructs)
        return ValidateLine(trimmed);
    }

    /// <summary>Validate a single line (or the full joined command).</summary>
    private static (bool Allowed, string Reason) ValidateLine(string text)
    {
        // Layer 1: Known dangerous patterns
        foreach (var (name, pattern) in DangerousPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return (false, $"Blocked by dangerous pattern [{name}]: command matched destructive signature");
            }
        }

        // Layer 2: Remote code execution
        foreach (var (name, pattern) in RemoteExecutionPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return (false, $"Blocked by remote execution pattern [{name}]: command attempts to fetch and execute remote code");
            }
        }

        // Layer 3: Injection structural detection
        foreach (var pattern in InjectionPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                var prefix = text[..Math.Min(match.Index, text.Length)];
                if (IsAllowedContext(prefix, text))
                    continue;

                return (false, $"Blocked by injection pattern: command contains dangerous construct '{match.Value}'");
            }
        }

        // Check for unsafe command prefixes
        var firstWord = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord != null && UnsafeCommandPrefixes.Contains(firstWord))
        {
            return (false, $"Blocked by privilege control: '{firstWord}' requires administrative privileges not granted in this context");
        }

        return (true, "");
    }

    /// <summary>
    /// Quick check if a command likely contains destructive intent.
    /// Used as a lightweight pre-check before full validation.
    /// </summary>
    public static bool IsObviouslyDangerous(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // Check dangerous pattern (more expensive regex)
        foreach (var (_, pattern) in DangerousPatterns)
        {
            if (pattern.IsMatch(command.Trim()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determine if an injection construct is in an allowed context.
    /// Currently very conservative — only allows echo/printf.
    /// </summary>
    private static bool IsAllowedContext(string commandPrefix, string fullCommand)
    {
        // Only allow subshell in echo/printf/cat with no piped execution
        if (commandPrefix.TrimStart().StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ||
            commandPrefix.TrimStart().StartsWith("printf ", StringComparison.OrdinalIgnoreCase))
        {
            // Ensure no pipe to dangerous destination
            foreach (var (_, pattern) in RemoteExecutionPatterns)
            {
                if (pattern.IsMatch(fullCommand))
                    return false;
            }
            return true;
        }

        return false;
    }
}
