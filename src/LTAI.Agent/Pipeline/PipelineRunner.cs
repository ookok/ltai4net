using System.Diagnostics;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline;

public sealed class PipelineRunner
{
    private readonly IReadOnlyList<StepEntry> _postSteps;
    private readonly ILogger<PipelineRunner> _logger;

    private sealed record StepEntry(
        string Name,
        Func<MessageContext, Task<MessageContext>> Execute,
        bool EnabledByDefault);

    public PipelineRunner(
        MemoryCachingStep? memoryCachingSave = null,
        CompactionStep? compaction = null,
        GrammarCheckStep? grammarCheck = null,
        QualityGateStep? qualityGate = null,
        DoDCheckStep? doDCheck = null,
        RetrospectiveStep? retrospective = null,
        ILogger<PipelineRunner>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRunner>.Instance;

        var steps = new List<StepEntry>
        {
            new("MemoryCaching(Save)", ctx => memoryCachingSave?.ProcessAsync(ctx) ?? Task.FromResult(ctx), memoryCachingSave != null),
            new("Compaction", ctx => compaction?.ProcessAsync(ctx) ?? Task.FromResult(ctx), compaction != null),
            new("GrammarCheck", ctx => grammarCheck?.ProcessAsync(ctx) ?? Task.FromResult(ctx), grammarCheck != null),
            new("QualityGate", ctx => qualityGate?.ProcessAsync(ctx) ?? Task.FromResult(ctx), qualityGate != null),
            new("DoDCheck", ctx => doDCheck?.ProcessAsync(ctx) ?? Task.FromResult(ctx), doDCheck != null),
            new("Retrospective", ctx => retrospective?.ProcessAsync(ctx) ?? Task.FromResult(ctx), retrospective != null),
        };

        _postSteps = steps.AsReadOnly();
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
