using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LTAI.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

[ToolDomain("sandbox")]
public sealed class ContainerSandboxProvider : IDisposable
{
    private readonly string _workspaceDir;
    private readonly string _sandboxMode;
    private readonly ILogger? _logger;
    private static int s_containerCounter;

    public ContainerSandboxProvider(
        string workspaceDir,
        string sandboxMode = "docker",
        ILogger? logger = null)
    {
        _workspaceDir = workspaceDir;
        _sandboxMode = sandboxMode;
        _logger = logger;
    }

    [Description("在隔离沙箱中执行一条 shell 命令。支持 Docker 容器隔离（每个任务独立容器）。")]
    [return: Description("命令的标准输出和退出码")]
    public async Task<string> ExecuteInSandbox(
        [Description("要执行的 shell 命令")] string command,
        [Description("超时秒数")] int timeoutSeconds = 60,
        [Description("工作目录（相对于沙箱）")] string? workdir = null)
    {
        if (_sandboxMode == "docker")
        {
            return await ExecuteInDockerAsync(command, timeoutSeconds, workdir).ConfigureAwait(false);
        }
        else
        {
            return await ExecuteLocallyAsync(command, timeoutSeconds, workdir).ConfigureAwait(false);
        }
    }

    [Description("将文件写入沙箱工作目录")]
    [return: Description("写入结果")]
    public async Task<string> WriteFileToSandbox(
        [Description("文件路径（相对沙箱工作目录）")] string path,
        [Description("文件内容")] string content)
    {
        var fullPath = Path.Combine(_workspaceDir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content).ConfigureAwait(false);
        return $"Written {content.Length} bytes to sandbox/{path}";
    }

    [Description("从沙箱工作目录读取文件")]
    [return: Description("文件内容")]
    public async Task<string> ReadFileFromSandbox(
        [Description("文件路径（相对沙箱工作目录）")] string path)
    {
        var fullPath = Path.Combine(_workspaceDir, path);
        if (!File.Exists(fullPath)) return $"Error: file not found: {path}";
        return await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
    }

    private async Task<string> ExecuteInDockerAsync(string command, int timeoutSeconds, string? workdir)
    {
        var containerId = $"ltai-sbx-{Process.GetCurrentProcess().Id}-{Interlocked.Increment(ref s_containerCounter)}";
        var imageName = "ubuntu:22.04";

        try
        {
            // Pull image if needed
            await RunProcessAsync("docker", $"pull {imageName}", timeoutSeconds: 120).ConfigureAwait(false);

            // Create container with workspace mount
            var mountArg = $"--mount type=bind,src=\"{_workspaceDir}\",dst=/mnt/workspace";
            var createCmd = $"create --name {containerId} -i {mountArg} --workdir /mnt/workspace --rm {imageName} bash";
            await RunProcessAsync("docker", createCmd, timeoutSeconds: 30).ConfigureAwait(false);

            // Copy command to temp script and execute
            var scriptContent = $"cd {workdir ?? "/mnt/workspace"} 2>/dev/null\n{command}\necho \"__EXIT_CODE=$?\"";
            var scriptPath = Path.Combine(Path.GetTempPath(), $"sbx-{containerId}.sh");
            await File.WriteAllTextAsync(scriptPath, scriptContent).ConfigureAwait(false);

            var copyCmd = $"cp {scriptPath} {containerId}:/tmp/run.sh";
            await RunProcessAsync("docker", copyCmd, timeoutSeconds: 15).ConfigureAwait(false);

            var execCmd = $"exec {containerId} bash /tmp/run.sh";
            var (output, _) = await RunProcessAsync("docker", execCmd, timeoutSeconds: timeoutSeconds).ConfigureAwait(false);

            // Cleanup
            _ = Task.Run(() => RunProcessAsync("docker", $"rm -f {containerId}", timeoutSeconds: 10));
            try { File.Delete(scriptPath); } catch { }

            // Extract exit code
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var exitCode = lines.Length > 0 && lines[^1].StartsWith("__EXIT_CODE=")
                ? lines[^1].Replace("__EXIT_CODE=", "")
                : "?";

            var result = string.Join("\n", lines.Take(lines.Length - 1));
            return $"Exit code: {exitCode}\n{result}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ContainerSandbox: Docker execution failed");
            // Fallback to local execution
            return await ExecuteLocallyAsync(command, timeoutSeconds, workdir).ConfigureAwait(false);
        }
    }

    private static async Task<string> ExecuteLocallyAsync(string command, int timeoutSeconds, string? workdir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workdir ?? Directory.GetCurrentDirectory(),
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var readTask = Task.Run(async () =>
            {
                output.Append(await proc.StandardOutput.ReadToEndAsync(cts.Token));
                error.Append(await proc.StandardError.ReadToEndAsync(cts.Token));
            }, cts.Token);

            if (!proc.WaitForExit((int)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds))
            {
                proc.Kill(entireProcessTree: true);
                return $"Error: command timed out after {timeoutSeconds}s\n{output}\n{error}";
            }

            await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}\n{output}\n{error}";
        }

        if (error.Length > 0)
            output.Append($"\n[stderr]\n{error}");

        return $"Exit code: {proc.ExitCode}\n{output}";
    }

    private static async Task<(string Output, string Error)> RunProcessAsync(string file, string args, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var output = await proc.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        var error = await proc.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        proc.WaitForExit((int)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds);

        return (output, error);
    }

    public void Dispose()
    {
        // Cleanup on disposal — handled per-execution
    }
}
