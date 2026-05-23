using LTAI.Tools.Capability.Governance;
using LTAI.Tools.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Evolution;

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
        _logger.LogInformation("ToolEvolutionLoop: Started (interval={Interval}, threshold={Threshold}, minInvocations={Min})",
            _checkInterval, _failureThreshold, _minInvocations);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await EvolveFailingToolsAsync(stoppingToken);
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

        _logger.LogInformation("ToolEvolutionLoop: Stopped");
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
                var evolvedTool = await EvolveSingleToolAsync(entry, ct);
                if (evolvedTool) evolved++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ToolEvolutionLoop: Failed to evolve {ToolName}", entry.Name);
            }
        }

        _logger.LogInformation("ToolEvolutionLoop: Cycle complete, evolved {Evolved}/{Total} tools",
            evolved, failing.Count);

        return evolved;
    }

    private async Task<bool> EvolveSingleToolAsync(ToolLifecycleEntry entry, CancellationToken ct)
    {
        var toolName = entry.Name;
        _logger.LogWarning("ToolEvolutionLoop: Evolving {ToolName} (successRate={Rate:F2}, invocations={Inv}, errors={Err})",
            toolName, entry.SuccessRate, entry.InvocationCount, entry.ErrorCount);

        if (_chatFn == null)
        {
            _logger.LogWarning("ToolEvolutionLoop: No chat function configured, skipping {ToolName}", toolName);
            return false;
        }

        // Step 1: Get tool from synthesizer to access its code
        var existingTool = _synthesizer.GetTool(toolName);
        string originalDescription;
        string originalCategory;

        if (existingTool != null)
        {
            originalDescription = existingTool.Description;
            originalCategory = existingTool.Category;
        }
        else
        {
            // Tool not in synthesizer — attempt to re-synthesize from description
            originalDescription = entry.Name.Replace('_', ' ');
            originalCategory = "general";
        }

        // Step 2: Use ToolMeta to generate improved version
        var errorContext = new List<string>
        {
            $"Tool '{toolName}' has success rate {entry.SuccessRate:F2}",
            $"Total invocations: {entry.InvocationCount}",
            $"Total errors: {entry.ErrorCount}",
            $"The tool needs to be more robust. Improve error handling, input validation, and fallback logic."
        };

        var evolveResult = await _toolMeta.SelfEvolve(
            toolName,
            existingTool?.Code ?? "No source available — synthesize from description",
            errorContext,
            _chatFn);

        if (!evolveResult.Applied || string.IsNullOrEmpty(evolveResult.RewrittenCode))
        {
            _logger.LogWarning("ToolEvolutionLoop: SelfEvolve failed for {ToolName}", toolName);
            return false;
        }

        _logger.LogInformation("ToolEvolutionLoop: Generated v2 code for {ToolName}: {Improvement}",
            toolName, evolveResult.Improvement[..Math.Min(evolveResult.Improvement.Length, 200)]);

        // Step 3: Synthesize the new version with security audit
        var synthesisResult = await _synthesizer.Synthesize(
            $"{originalDescription}\n\nImprovement: {evolveResult.Improvement}",
            originalCategory,
            _chatFn);

        if (!synthesisResult.Success)
        {
            _logger.LogWarning("ToolEvolutionLoop: Synthesis audit failed for {ToolName}: {Error}",
                toolName, synthesisResult.Error);
            return false;
        }

        // Step 4: Register evolved tool in lifecycle
        _lifecycle.Register(synthesisResult.Tool!.Name, "2.0.0", ToolLifecycleState.Experimental);
        _lifecycle.Deprecate(toolName, synthesisResult.Tool.Name,
            $"Auto-evolved to v2 at {DateTime.UtcNow:yyyy-MM-dd HH:mm}");

        _logger.LogInformation("ToolEvolutionLoop: Successfully evolved {OldName} → {NewName} (v2, experimental)",
            toolName, synthesisResult.Tool.Name);

        return true;
    }
}
