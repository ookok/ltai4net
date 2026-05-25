using LTAI.Models;

namespace LTAI.Infra.Sandbox;

public sealed class SandboxExecutorAdapter : ISandboxExecutor
{
    private readonly SandboxOrchestrator _orchestrator;

    public SandboxExecutorAdapter(SandboxOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task<SandboxExecutionResult> ExecuteCommandAsync(
        string command,
        int timeoutSeconds = 30,
        int memoryMb = 256,
        bool allowNetwork = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _orchestrator.ExecuteAsync(
                command,
                SandboxLanguage.Shell,
                timeoutSeconds,
                memoryMb,
                allowNetwork,
                cancellationToken).ConfigureAwait(false);

            return new SandboxExecutionResult
            {
                Success = result.Success,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                ExitCode = result.ExitCode,
                ExecutionTimeMs = result.ExecutionTimeMs,
                PeakMemoryKb = result.PeakMemoryKb,
                Error = result.Error,
                TimedOut = result.TimedOut,
                Sandboxed = true
            };
        }
        catch (Exception ex)
        {
            return new SandboxExecutionResult
            {
                Success = false,
                Error = $"Sandbox error: {ex.Message}",
                Sandboxed = false
            };
        }
    }
}
