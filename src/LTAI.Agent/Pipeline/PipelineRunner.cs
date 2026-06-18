using System.Diagnostics;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline;

public sealed class PipelineRunner
{
    private readonly IReadOnlyList<StepEntry> _postSteps;
    private readonly ILogger<PipelineRunner> _logger;

    /// <summary>Default post-generation step order (name → execution index).</summary>
    private static readonly Dictionary<string, int> DefaultStepOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MemoryCaching(Save)"] = 0,
        ["Compaction"] = 1,
        ["GrammarCheck"] = 2,
        ["QualityGate"] = 3,
        ["DoDCheck"] = 4,
        ["Retrospective"] = 5,
    };

    private sealed record StepEntry(
        string Name,
        Func<MessageContext, Task<MessageContext>> Execute,
        bool EnabledByDefault);

    /// <summary>
    /// DI-friendly constructor: collects all registered <see cref="IPipelineStep"/>
    /// instances and orders them by <see cref="DefaultStepOrder"/>.
    /// Steps not in the order map are skipped.
    /// </summary>
    public PipelineRunner(
        IEnumerable<IPipelineStep> steps,
        ILogger<PipelineRunner>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRunner>.Instance;

        _postSteps = steps
            .Where(s => DefaultStepOrder.ContainsKey(s.Name))
            .OrderBy(s => DefaultStepOrder[s.Name])
            .Select(s => new StepEntry(s.Name, s.ProcessAsync, true))
            .ToList();
    }

    public async Task<MessageContext> RunPostGenerationAsync(MessageContext context)
    {
        return await RunStepsAsync(context, _postSteps).ConfigureAwait(false);
    }

    private async Task<MessageContext> RunStepsAsync(MessageContext context, IReadOnlyList<StepEntry> steps)
    {
        foreach (var step in steps)
        {
            if (!step.EnabledByDefault)
            {
                _logger.LogDebug("PipelineRunner: skipping step '{Name}' (not configured)", step.Name);
                continue;
            }

            if (context.SafetyBlocked)
            {
                _logger.LogInformation("PipelineRunner: stopping at '{Name}' (safety blocked)", step.Name);
                break;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                context = await step.Execute(context).ConfigureAwait(false);

                if (context.TryGet<bool>("GrammarCheckBlocked", out var gramBlocked) && gramBlocked)
                {
                    _logger.LogInformation("PipelineRunner: step '{Name}' blocked (grammar check)", step.Name);
                    break;
                }
                if (context.TryGet<bool>("QualityGateBlocked", out var qgBlocked) && qgBlocked)
                {
                    _logger.LogInformation("PipelineRunner: step '{Name}' blocked (quality gate)", step.Name);
                    break;
                }
                if (context.TryGet<bool>("DoDBlocked", out var dodBlocked) && dodBlocked)
                {
                    _logger.LogInformation("PipelineRunner: step '{Name}' blocked (DoD)", step.Name);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipelineRunner: step '{Name}' threw an exception", step.Name);
                context.Set("PipelineError", ex.Message);
                break;
            }

            sw.Stop();
            _logger.LogDebug("PipelineRunner: step '{Name}' completed in {ElapsedMs}ms",
                step.Name, sw.ElapsedMilliseconds);
        }

        return context;
    }
}
