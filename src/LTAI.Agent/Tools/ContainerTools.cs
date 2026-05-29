using System.ComponentModel;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;

namespace LTAI.Agent.Tools;

/// <summary>
/// Container execution tools using Docker via MAF DockerShellExecutor.
/// Sandboxed: no network, read-only FS, nobody user, resource-limited.
/// </summary>
public sealed class ContainerTools
{
    [Description("Execute a command in an isolated Docker sandbox (no network, read-only)")]
    public async Task<string> RunInContainer(
        [Description("Shell command to execute")] string command,
        [Description("Timeout in seconds (5-300)")] int timeoutSec = 60)
    {
        await using var executor = new DockerShellExecutor(new DockerShellExecutorOptions
        {
            Image = "mcr.microsoft.com/azurelinux/base/core:3.0",
            Network = "none",
            ReadOnlyRoot = true,
            MemoryBytes = 512 * 1024 * 1024,
            PidsLimit = 256,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 300)),
            MaxOutputBytes = 64 * 1024,
        });

        try
        {
            var result = await executor.RunAsync(command);
            return FormatResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Container execution failed: {ex.Message}";
        }
    }

    [Description("Run a command in a container with network access (for package installs)")]
    public async Task<string> RunWithNetwork(
        [Description("Shell command to execute")] string command,
        [Description("Timeout in seconds (5-300)")] int timeoutSec = 120)
    {
        await using var executor = new DockerShellExecutor(new DockerShellExecutorOptions
        {
            Image = "mcr.microsoft.com/azurelinux/base/core:3.0",
            Network = "bridge",
            ReadOnlyRoot = true,
            MemoryBytes = 512 * 1024 * 1024,
            PidsLimit = 256,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 300)),
            MaxOutputBytes = 64 * 1024,
        });

        try
        {
            var result = await executor.RunAsync(command);
            return FormatResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Container execution failed: {ex.Message}";
        }
    }

    [Description("Check if Docker is available on this system")]
    public static async Task<string> CheckDockerAsync()
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info --format '{{.ServerVersion}}'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return $"✅ Docker {output.Trim()}";

            return $"❌ Docker not available: {error.Trim()}";
        }
        catch (Exception ex)
        {
            return $"❌ Docker check failed: {ex.Message}";
        }
    }

    private static string FormatResult(ShellResult result)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(result.Stdout))
            sb.Append(result.Stdout);
        if (!string.IsNullOrEmpty(result.Stderr))
            sb.Append($"\nSTDERR:\n{result.Stderr}");
        if (result.ExitCode != 0)
            sb.Append($"\n(exit code: {result.ExitCode})");
        if (result.TimedOut)
            sb.Append("\n⚠️ Command timed out");
        if (result.Truncated)
            sb.Append("\n⚠️ Output truncated");
        return sb.ToString();
    }
}
