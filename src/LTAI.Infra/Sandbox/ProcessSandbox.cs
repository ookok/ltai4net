using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Sandbox;

public sealed class ProcessSandbox : ISandbox
{
    private readonly ILogger<ProcessSandbox> _logger;
    private readonly Dictionary<SandboxLanguage, string> _runtimes = new()
    {
        [SandboxLanguage.Python] = "python3",
        [SandboxLanguage.JavaScript] = "node",
        [SandboxLanguage.CSharp] = "dotnet-script",
        [SandboxLanguage.Shell] = RuntimeInfo.IsWindows ? "pwsh" : "bash"
    };

    public string Name => "ProcessSandbox";
    public SandboxCapability Capability => SandboxCapability.Python | SandboxCapability.JavaScript |
        SandboxCapability.CSharp | SandboxCapability.Shell | SandboxCapability.Timeout;

    public ProcessSandbox(ILogger<ProcessSandbox> logger)
    {
        _logger = logger;
    }

    public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string? tempFile = null;

        try
        {
            var (executable, args, tf) = PrepareExecution(request);
            tempFile = tf;
            var psi = new ProcessStartInfo(executable, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = request.WorkingDirectory ?? Path.GetTempPath()
            };

            using var process = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!string.IsNullOrEmpty(request.Stdin))
            {
                await process.StandardInput.WriteAsync(request.Stdin).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* non-fatal */ }
                sw.Stop();
                return new SandboxResult
                {
                    Success = false, TimedOut = true, Stdout = stdout.ToString(),
                    Stderr = stderr.ToString(), ExecutionTimeMs = sw.ElapsedMilliseconds,
                    Error = $"Execution timed out after {request.TimeoutSeconds}s"
                };
            }

            sw.Stop();
            Cleanup(tempFile);

            var memKb = 0L;
            if (!process.HasExited)
            {
                try { memKb = process.PeakWorkingSet64 / 1024; } catch { }
            }

            return new SandboxResult
            {
                Success = process.ExitCode == 0,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                ExitCode = process.ExitCode,
                ExecutionTimeMs = sw.ElapsedMilliseconds,
                PeakMemoryKb = memKb
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Cleanup(tempFile);
            _logger.LogError(ex, "Sandbox execution failed");
            return new SandboxResult
            {
                Success = false, Error = ex.Message, ExecutionTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var (lang, runtime) in _runtimes)
            {
                var psi = new ProcessStartInfo(RuntimeInfo.IsWindows ? "where" : "which", runtime)
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await p.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (p.ExitCode != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (string executable, string args, string? tempFile) PrepareExecution(SandboxRequest request)
    {
        var runtime = _runtimes.GetValueOrDefault(request.Language, "python3");
        return request.Language switch
        {
            SandboxLanguage.Python => (runtime, $"-c \"{EscapeArg(request.Code)}\"", null),
            SandboxLanguage.JavaScript => (runtime, $"-e \"{EscapeArg(request.Code)}\"", null),
            SandboxLanguage.Shell => (RuntimeInfo.IsWindows ? "pwsh" : "bash",
                RuntimeInfo.IsWindows ? $"-NoProfile -Command \"{EscapeArg(request.Code)}\"" : $"-c \"{EscapeArg(request.Code)}\"", null),
            SandboxLanguage.CSharp => PrepareCSharp(request.Code),
            _ => (runtime, $"-c \"{EscapeArg(request.Code)}\"", null)
        };
    }

    private (string executable, string args, string? tempFile) PrepareCSharp(string code)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ltaisb_{Guid.NewGuid():N}.csx");
        File.WriteAllText(tempFile, code);
        return ("dotnet-script", tempFile, tempFile);
    }

    private static string EscapeArg(string arg) => arg.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Cleanup(string? tempFile)
    {
        if (tempFile != null)
        {
            try { File.Delete(tempFile); } catch { /* non-fatal */ }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class RuntimeInfo
{
    public static bool IsWindows => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.Windows);
}
