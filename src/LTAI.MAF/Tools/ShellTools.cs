using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LTAI.MAF.Tools;

[Description("Command-line and script execution tools")]
public sealed class ShellTools
{
    private static readonly HashSet<string> _dangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf /", "rm -rf /*", "del /f /s C:\\", "format", "shutdown /s", "shutdown -h",
        ":(){ :|:& };:", "mkfs", "dd if=/dev/zero", "> /dev/sda"
    };

    [Description("Execute a shell command and return stdout/stderr/exit code. Commands timeout after 60 seconds. DANGEROUS commands are blocked.")]
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

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var completed = await Task.Run(() => process.WaitForExit(60_000), cancellationToken);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return JsonSerializer.Serialize(new { error = "Command timed out after 60s" });
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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
            machineName = Environment.MachineName,
            user = Environment.UserName,
            runtime = RuntimeInformation.FrameworkDescription,
            workingSet = Process.GetCurrentProcess().WorkingSet64
        });
    }
}
