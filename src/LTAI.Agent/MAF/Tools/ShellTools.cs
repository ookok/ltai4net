using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LTAI.Core.Governors;

namespace LTAI.Agent.Tools;

[Description("Command-line and script execution tools")]
public sealed class ShellTools
{
    /// <summary>
    /// External safety gate delegate — wired to UnifiedSafetyGate.EvaluateToolCall at DI startup.
    /// Signature: (toolName, input) => isSafe. Set by ServiceCollectionExtensions.
    /// </summary>
    public static Func<string, string, bool>? ExternalSafetyGate { get; set; }

    private static readonly HashSet<string> _dangerousPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf /", "rm -rf /*", "del /f /s C:\\", "format", "shutdown", "reboot",
        ":(){ :|:& };:", "mkfs", "dd if=/dev/zero", "> /dev/sda",
        "chmod 777 /", "wget", "curl", "nc ", "ncat ",
        "sudo ", "su ", "passwd", "chown", "kill -9 -1", "killall"
    };

    private static readonly char[] _shellMetacharacters = { ';', '&', '|', '`', '$', '>', '<', '\n', '\r' };

    [Description("Execute a shell command and return stdout/stderr/exit code. Commands timeout after 60 seconds. DANGEROUS commands are blocked. Command is piped via stdin for safety.")]
    public static async Task<string> ExecuteCommand(
        [Description("The shell command to execute")] string command,
        [Description("Working directory for the command")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return JsonSerializer.Serialize(new { error = "Command cannot be empty" });

        if (command.Length > 4096)
            return JsonSerializer.Serialize(new { error = "Command exceeds 4096 character limit" });

        foreach (var dangerous in _dangerousPatterns)
        {
            if (command.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { error = $"Blocked dangerous command pattern: {dangerous}" });
        }

        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.IndexOfAny(_shellMetacharacters) >= 0)
            {
                if (word.StartsWith('$') && word.Length > 1 && word.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '{' || c == '}'))
                    continue;
                return JsonSerializer.Serialize(new { error = $"Blocked shell metacharacter in command: '{word}'" });
            }
        }

        // UnifiedSafetyGate integration (wired at DI startup)
        if (ExternalSafetyGate != null && !ExternalSafetyGate("shell", command))
            return JsonSerializer.Serialize(new { error = "Blocked by external safety gate" });

        string shellExe;
        string[] shellArgs;
        if (OperatingSystem.IsWindows())
        {
            shellExe = "pwsh";
            shellArgs = new[] { "-NoProfile", "-NonInteractive", "-Command", "-" };
        }
        else
        {
            shellExe = "/bin/bash";
            shellArgs = new[] { "--noprofile", "--norc" };
        }

        if (MicroKernel.Default != null)
        {
            var kResult = await MicroKernel.Default.ExecuteAsync(new KernelOp
            {
                Command = shellExe,
                Arguments = string.Join(" ", shellArgs),
                Stdin = command,
                WorkingDirectory = workingDirectory,
                Timeout = TimeSpan.FromSeconds(60)
            }, cancellationToken).ConfigureAwait(false);

            var kOut = kResult.Data ?? "";
            var kErr = kResult.Error ?? "";
            if (kOut.Length > 10000) kOut = kOut[..10000] + "\n... (truncated)";
            if (kErr.Length > 10000) kErr = kErr[..10000] + "\n... (truncated)";

            return JsonSerializer.Serialize(new { exitCode = kResult.Success ? 0 : 1, stdout = kOut, stderr = kErr, command });
        }

        var psi = new ProcessStartInfo
        {
            FileName = shellExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in shellArgs)
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var completed = await Task.Run(() => process.WaitForExit(60_000), cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return JsonSerializer.Serialize(new { error = "Command timed out after 60s" });
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (stdout.Length > 10000) stdout = stdout[..10000] + "\n... (truncated)";
        if (stderr.Length > 10000) stderr = stderr[..10000] + "\n... (truncated)";

        return JsonSerializer.Serialize(new { exitCode = process.ExitCode, stdout, stderr, command });
    }

    [Description("Get the current working directory and environment info.")]
    public static string GetEnvironmentInfo()
    {
        return JsonSerializer.Serialize(new
        {
            currentDirectory = Environment.CurrentDirectory,
            os = $"{Environment.OSVersion} ({RuntimeInformation.OSDescription})",
            is64Bit = Environment.Is64BitOperatingSystem,
            processorCount = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription
        });
    }
}
