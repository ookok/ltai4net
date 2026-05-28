using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LTAI.Core.Governors;

namespace LTAI.Tools.General;

public static class ShellTools
{
    // Security: external safety gate delegate — wired to UnifiedSafetyGate at DI startup
    // Signature: (toolName, input) => isSafe
    public static Func<string, string, bool>? ExternalSafetyGate { get; set; }

    // Security: dangerous command patterns — aligned with ShellEnv + MAF ShellTools
    private static readonly HashSet<string> DangerousPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf /", "rm -rf /*", "rm -rf ~", "rm -rf .",
        "del /f /s C:\\", "del /f /s /q",
        "format", "mkfs",
        "shutdown", "reboot", "halt", "poweroff",
        ":(){ :|:& };:", "fork bomb",
        "dd if=/dev/zero", "dd if=/dev/urandom",
        "> /dev/sda", "> /dev/hda",
        "chmod 777 /", "chmod -R 777",
        "wget ", "curl ",
        " | sh", " | bash", " | pwsh",
        "reg delete", "reg add",
        "sc stop", "sc delete",
        "taskkill /f", "taskkill /im",
        "Remove-Item -Recurse", "Remove-Item -Force",
        "sudo ", "su ", "passwd", "chown",
        "nc ", "ncat ", "eval ", "exec(",
        "crontab -", "base64 -d",
    };

    private static readonly char[] ShellMetacharacters = { ';', '&', '|', '`', '$', '>', '<', '\n', '\r' };

    /// <summary>
    /// Validate a command against dangerous patterns and the external safety gate.
    /// Returns null if safe, or a block reason string if dangerous.
    /// </summary>
    public static string? ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Command cannot be empty";

        if (command.Length > 4096)
            return "Command exceeds 4096 character limit";

        // Layer 1: dangerous pattern matching
        var normalized = command.ToLowerInvariant().Replace(" ", "");
        foreach (var pattern in DangerousPatterns)
        {
            var normalizedPattern = pattern.ToLowerInvariant().Replace(" ", "");
            if (normalized.Contains(normalizedPattern))
                return $"Blocked dangerous command pattern: '{pattern}'";
        }

        // Layer 2: shell metacharacter check (allow $VAR references)
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.IndexOfAny(ShellMetacharacters) >= 0)
            {
                // Allow environment variable references like $HOME or ${HOME}
                if (word.StartsWith('$') && word.Length > 1 &&
                    word.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '{' || c == '}'))
                    continue;
                return $"Blocked shell metacharacter in command: '{word}'";
            }
        }

        // Layer 3: External safety gate (wired to UnifiedSafetyGate at DI startup)
        if (ExternalSafetyGate != null && !ExternalSafetyGate("shell", command))
            return "Blocked by external safety gate";

        return null; // safe
    }

    [Description("Executes a shell command and returns the output")]
    public static async Task<string> ExecuteAsync(
        [Description("The shell command to execute")] string command,
        [Description("Working directory for the command")] string? workingDirectory = null,
        CancellationToken ct = default)
    {
        const int MaxOutputChars = 50000;

        // Security: validate command before execution
        var blockReason = ValidateCommand(command);
        if (blockReason != null)
            return $"[BLOCKED] {blockReason}";

        try
        {
            // Prefer MicroKernel sandbox when available
            if (MicroKernel.Default != null)
            {
                try
                {
                    var shellExe = OperatingSystem.IsWindows() ? "pwsh" : "/bin/bash";
                    var shellArgs = OperatingSystem.IsWindows()
                        ? $"-NoProfile -NonInteractive -Command -"
                        : "--noprofile --norc";

                    var kResult = await MicroKernel.Default.ExecuteAsync(new KernelOp
                    {
                        Command = shellExe,
                        Arguments = shellArgs,
                        Stdin = command,
                        WorkingDirectory = workingDirectory,
                        Timeout = TimeSpan.FromSeconds(30)
                    }, ct).ConfigureAwait(false);

                    var kOut = kResult.Data ?? "";
                    var kErr = kResult.Error ?? "";
                    if (kOut.Length > MaxOutputChars)
                        kOut = kOut[..MaxOutputChars] + "\n... [truncated]";
                    if (kErr.Length > MaxOutputChars)
                        kErr = kErr[..MaxOutputChars] + "\n... [truncated]";

                    var kCombined = kOut;
                    if (!string.IsNullOrEmpty(kErr))
                        kCombined += $"\n[stderr]\n{kErr}";
                    return kCombined.Length > 0 ? kCombined : $"(exit code: {(kResult.Success ? 0 : 1)})";
                }
                catch
                {
                    // Fall through to Process.Start fallback
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "pwsh" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"-NoProfile -Command \"{command}\"" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            using var process = Process.Start(psi);
            if (process is null) return "Failed to start process.";

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var result = stdout;
            if (!string.IsNullOrEmpty(stderr))
                result += $"\n[stderr]\n{stderr}";

            if (result.Length > MaxOutputChars)
            {
                var truncated = result[..MaxOutputChars];
                return $"{truncated}\n\n[Output truncated: {result.Length} chars total, showing first {MaxOutputChars} chars]";
            }

            return result.Length > 0 ? result : $"(exit code: {process.ExitCode})";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }

    [Description("Gets the current working directory")]
    public static string GetWorkingDirectory()
    {
        return Environment.CurrentDirectory;
    }

    [Description("Gets environment information (OS, runtime, architecture)")]
    public static string GetEnvironmentInfo()
    {
        return $"OS: {Environment.OSVersion}\n"
             + $"Runtime: {Environment.Version}\n"
             + $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n"
             + $"Machine: {Environment.MachineName}\n"
             + $"User: {Environment.UserName}";
    }

    [Description("Reads an environment variable")]
    public static string? GetEnvironmentVariable(
        [Description("Name of the environment variable")] string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }
}
