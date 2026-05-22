using System.Diagnostics;
using Microsoft.Agents.AI;

namespace LTAI.Tools.General;

public static class ShellTools
{
    [AIFunction("Executes a shell command and returns the output")]
    public static async Task<string> ExecuteAsync(
        [AIFunctionParameter("The shell command to execute", Required = true)]
        string command,
        [AIFunctionParameter("Working directory for the command")]
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "pwsh" : "bash",
                Arguments = OperatingSystem.IsWindows() ? $"-NoProfile -Command \"{command}\"" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            using var process = Process.Start(psi);
            if (process is null) return "Failed to start process.";

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var result = stdout;
            if (!string.IsNullOrEmpty(stderr))
                result += $"\n[stderr]\n{stderr}";

            return result.Length > 0 ? result : $"(exit code: {process.ExitCode})";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }

    [AIFunction("Gets the current working directory")]
    public static string GetWorkingDirectory()
    {
        return Environment.CurrentDirectory;
    }

    [AIFunction("Gets environment information (OS, runtime, architecture)")]
    public static string GetEnvironmentInfo()
    {
        return $"OS: {Environment.OSVersion}\n"
             + $"Runtime: {Environment.Version}\n"
             + $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n"
             + $"Machine: {Environment.MachineName}\n"
             + $"User: {Environment.UserName}";
    }

    [AIFunction("Reads an environment variable")]
    public static string? GetEnvironmentVariable(
        [AIFunctionParameter("Name of the environment variable", Required = true)]
        string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }
}
