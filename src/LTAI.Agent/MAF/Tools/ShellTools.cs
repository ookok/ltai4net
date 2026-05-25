using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("Command-line and script execution tools")]
public sealed class ShellTools
{
    private static readonly HashSet<string> _dangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf /", "rm -rf /*", "del /f /s C:\\", "format", "shutdown /s", "shutdown -h",
        ":(){ :|:& };:", "mkfs", "dd if=/dev/zero", "> /dev/sda"
    };

    [Description("Execute a shell command and return stdout/stderr/exit code. Commands timeout after 60 seconds. DANGEROUS commands are blocked. Command is piped via stdin for safety.")]
    public static async Task<string> ExecuteCommand(
        [Description("The shell command to execute")] string command,
        [Description("Working directory for the command")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var dangerous in _dangerousCommands)
        {
            if (command.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { error = $"Blocked dangerous command pattern: {dangerous}" });
        }

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
