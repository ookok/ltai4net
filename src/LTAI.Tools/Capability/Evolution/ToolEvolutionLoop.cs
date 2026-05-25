using System.Collections.Concurrent;
using LTAI.Tools.Capability.Governance;
using LTAI.Tools.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Evolution;

public sealed class RollbackHistory
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _rollbacks = new();
    private const int RollbackThreshold = 3;
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public bool RecordRollback(string toolName)
    {
        var list = _rollbacks.GetOrAdd(toolName, _ => new List<DateTime>());
        lock (list)
        {
            var cutoff = DateTime.UtcNow - Window;
            list.RemoveAll(dt => dt < cutoff);
            list.Add(DateTime.UtcNow);
            return list.Count >= RollbackThreshold;
        }
    }

    public int GetRollbackCount(string toolName) =>
        _rollbacks.TryGetValue(toolName, out var list) ? list.Count : 0;
}

public sealed class ToolEvolutionLoop : BackgroundService
{
    private readonly ILogger<ToolEvolutionLoop> _logger;
    private readonly ToolLifecycle _lifecycle = ToolLifecycle.Instance;
    private readonly ToolMeta _toolMeta;
    private readonly ToolSynthesizer _synthesizer;
    private readonly Func<string, string, Task<string>>? _chatFn;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
    private readonly double _failureThreshold = 0.5;
    private readonly int _minInvocations = 3;
    private readonly RollbackHistory _rollbackHistory = new();

    public bool ObservationMode { get; set; } = true;
    public bool AutoPromote { get; set; }
    public event Func<string, string, CancellationToken, Task>? P0Alert;

    public ToolEvolutionLoop(
        ILogger<ToolEvolutionLoop> logger,
        ToolMeta toolMeta,
        ToolSynthesizer synthesizer,
        Func<string, string, Task<string>>? chatFn = null)
    {
        _logger = logger;
        _toolMeta = toolMeta;
        _synthesizer = synthesizer;
        _chatFn = chatFn;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = ObservationMode ? "OBSERVATION (monitor only)" : "ACTIVE (auto-evolve)";
        _logger.LogInformation("ToolEvolutionLoop: Started [{Mode}] interval={Interval} threshold={Threshold}",
            mode, _checkInterval, _failureThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken).ConfigureAwait(false);
                await EvolveFailingToolsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ToolEvolutionLoop: Evolution cycle failed");
            }
        }
    }

    public async Task<int> EvolveFailingToolsAsync(CancellationToken ct = default)
    {
        var failing = _lifecycle.GetFailing(_failureThreshold, _minInvocations);
        if (failing.Count == 0) return 0;

        _logger.LogInformation("ToolEvolutionLoop: Found {Count} failing tools", failing.Count);
        var evolved = 0;

        foreach (var entry in failing)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (ObservationMode)
                {
                    _logger.LogInformation("ToolEvolution OBSERVE: {Tool} errorRate={Rate:P2} ({Errors}/{Total}) — would evolve but ObservationMode is active",
                        entry.Name, 1.0 - entry.SuccessRate, entry.ErrorCount, entry.InvocationCount);
                    continue;
                }

                // Rollback storm guard: freeze if 3+ rollbacks in 24h
                if (_rollbackHistory.GetRollbackCount(entry.Name) >= 3)
                {
                    _logger.LogError("ToolEvolution: {Tool} FROZEN — {Count} rollbacks in 24h. P0 alert triggered.",
                        entry.Name, _rollbackHistory.GetRollbackCount(entry.Name));
                    if (P0Alert != null)
                        await P0Alert(entry.Name, "FROZEN: 3+ rollbacks in 24h", ct);
                    continue;
                }

                var ok = await EvolveSingleToolAsync(entry, ct).ConfigureAwait(false);
                if (ok) evolved++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ToolEvolutionLoop: Failed to evolve {ToolName}", entry.Name);
                _rollbackHistory.RecordRollback(entry.Name);
            }
        }

        if (evolved > 0)
            _logger.LogInformation("ToolEvolutionLoop: Cycle complete, evolved {Evolved}/{Total} tools", evolved, failing.Count);

        return evolved;
    }

    public async Task DryRunCycleAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("ToolEvolution DryRun: scanning for failing tools...");
        var failing = _lifecycle.GetFailing(0.3, minInvocations: 5).ToList();
        foreach (var tool in failing)
            _logger.LogInformation("ToolEvolution DryRun: {Tool} eligible (errorRate={Rate:P2})", tool.Name, 1.0 - tool.SuccessRate);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["observation_mode"] = ObservationMode,
        ["auto_promote"] = AutoPromote,
        ["failing_tools"] = _lifecycle.GetFailing(0.3).Count(),
        ["rollback_events_24h"] = _rollbackHistory.GetRollbackCount("global")
    };

    // ... existing EvolveSingleToolAsync remains unchanged
    private async Task<bool> EvolveSingleToolAsync(ToolLifecycleEntry entry, CancellationToken ct)
    {
        var toolName = entry.Name;
        _logger.LogWarning("ToolEvolutionLoop: Evolving {ToolName} (successRate={Rate:F2})", toolName, entry.SuccessRate);

        if (_chatFn == null)
        {
            _logger.LogWarning("ToolEvolutionLoop: No chat function configured, skipping {ToolName}", toolName);
            return false;
        }

        var existingTool = _synthesizer.GetTool(toolName);
        var originalDescription = existingTool?.Description ?? entry.Name.Replace('_', ' ');
        var originalCategory = existingTool?.Category ?? "general";

        var errorContext = new List<string>
        {
            $"Tool '{toolName}' has success rate {entry.SuccessRate:F2}",
            $"Total errors: {entry.ErrorCount}"
        };

        var evolveResult = await _toolMeta.SelfEvolve(toolName,
            existingTool?.Code ?? "No source available", errorContext, _chatFn);

        if (!evolveResult.Applied || string.IsNullOrEmpty(evolveResult.RewrittenCode))
            return false;

        var synthesisResult = await _synthesizer.Synthesize(
            $"{originalDescription}\n\nImprovement: {evolveResult.Improvement}",
            originalCategory, _chatFn);

        if (!synthesisResult.Success) return false;

        _lifecycle.Register(synthesisResult.Tool!.Name, "2.0.0", ToolLifecycleState.Experimental);
        _lifecycle.Deprecate(toolName, synthesisResult.Tool.Name,
            $"Auto-evolved to v2 at {DateTime.UtcNow:yyyy-MM-dd HH:mm}");

        _logger.LogInformation("ToolEvolutionLoop: Evolved {OldName} → {NewName}", toolName, synthesisResult.Tool.Name);
        return true;
    }
}
