using LTAI.AI.Interfaces;
using LTAI.Planning.Planning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

/// <summary>
/// Startup recovery: restores planning checkpoints and agent state on boot.
/// </summary>
public sealed class StartupRecoveryService : IHostedService
{
    private readonly ILivingTreeSystem _lts;
    private readonly TaskCheckpoint _checkpoint;
    private readonly ILogger<StartupRecoveryService> _logger;

    public StartupRecoveryService(
        ILivingTreeSystem lts, TaskCheckpoint checkpoint,
        ILogger<StartupRecoveryService> logger)
    {
        _lts = lts; _checkpoint = checkpoint;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Fire-and-forget: don't block host startup on recovery.
        // Recovery runs in background; failures are logged, not fatal.
        _ = Task.Run(async () =>
        {
            try { await RecoverAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Startup recovery failed (non-fatal)"); }
        }, ct);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Recovery: restoring state...");

        var sessions = _checkpoint.ListSessions();
        var restored = 0;
        foreach (var (sid, _) in sessions.Take(10))
        {
            try
            {
                var state = await _checkpoint.LoadAsync(sid).ConfigureAwait(false);
                if (state != null) restored++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Restore session {Id} failed", sid); }
        }

        try
        {
            await _lts.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Agent init failed"); }

        _logger.LogInformation("Recovery: done (sessions={Sessions}, mode={Mode})", restored, _lts.Mode);
    }
}
