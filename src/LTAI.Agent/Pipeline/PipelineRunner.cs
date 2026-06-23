using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline;

public sealed class PipelineRunner
{
    private readonly IReadOnlyList<StepGroup> _preGroups;
    private readonly IReadOnlyList<StepGroup> _postGroups;
    private readonly ILogger<PipelineRunner> _logger;

    /// <summary>Default pre-generation step order (name → execution index).
    /// Steps with the same index run sequentially in definition order.</summary>
    private static readonly Dictionary<string, int> PreStepOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LoraAdapter"] = 0,
        ["MemoryCaching(Restore)"] = 1,
        ["RagContext"] = 2,
        ["ProgressGuard"] = 3,
        ["ProactiveSuggest"] = 4,
        ["SafetyCheck"] = 5,
        ["Router"] = 6,
        ["ToolExecution"] = 7,
    };

    /// <summary>
    /// Post-generation step plan. Each entry defines a group:
    ///   order: execution sequence (lower runs first)
    ///   parallel: if true, all steps in the group run concurrently
    ///   names: step names in this group
    ///
    /// Design: DeltaAnchor → MemoryCaching(Save) → Compaction are sequential
    /// (they modify context data). Check steps (GrammarCheck, AntiPatternCheck,
    /// QualityGate, DoDCheck) are independent readers and run in parallel for
    /// latency reduction. Retrospective runs last.
    /// </summary>
    private static readonly (int Order, bool Parallel, string[] Names)[] PostStepPlan = [
        (0,  false, ["DeltaAnchor"]),
        (1,  false, ["MemoryCaching(Save)"]),
        (2,  false, ["Compaction"]),
        (3,  false, ["DiscoursePlanning"]),
        (4,  true,  ["GrammarCheck", "AntiPatternCheck", "QualityGate", "DoDCheck", "ThinkingTag"]),
        (5,  false, ["Retrospective"]),
    ];

    private sealed record StepEntry(
        string Name,
        Func<MessageContext, Task<MessageContext>> Execute);

    private sealed record StepGroup(
        bool IsParallel,
        IReadOnlyList<StepEntry> Steps);

    /// <summary>
    /// DI-friendly constructor: collects all registered <see cref="IPipelineStep"/>
    /// instances and orders them by <see cref="PreStepOrder"/> and <see cref="PostStepPlan"/>.
    /// Steps not in either order map are skipped.
    /// </summary>
    public PipelineRunner(
        IEnumerable<IPipelineStep> steps,
        ILogger<PipelineRunner>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRunner>.Instance;

        var stepMap = steps.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        _preGroups = BuildSequentialGroups(stepMap, PreStepOrder);

        _postGroups = PostStepPlan
            .Select(plan =>
            {
                var stepEntries = plan.Names
                    .Select(n => stepMap.GetValueOrDefault(n))
                    .Where(s => s != null)
                    .Select(s => new StepEntry(s.Name, s.ProcessAsync))
                    .ToList();
                return stepEntries.Count > 0
                    ? new StepGroup(plan.Parallel, stepEntries)
                    : null;
            })
            .Where(g => g != null)
            .ToList()!;
    }

    /// <summary>Run the pre-generation pipeline (before LLM call).</summary>
    public async Task<MessageContext> RunPreGenerationAsync(MessageContext context)
    {
        foreach (var group in _preGroups)
            context = await RunSequentialGroupAsync(context, group.Steps).ConfigureAwait(false);
        return context;
    }

    /// <summary>Run the post-generation pipeline (after LLM call).</summary>
    public async Task<MessageContext> RunPostGenerationAsync(MessageContext context)
    {
        foreach (var group in _postGroups)
        {
            context = group.IsParallel
                ? await RunParallelGroupAsync(context, group.Steps).ConfigureAwait(false)
                : await RunSequentialGroupAsync(context, group.Steps).ConfigureAwait(false);

            if (ShouldBreak(context)) break;
        }
        return context;
    }

    private static List<StepGroup> BuildSequentialGroups(
        Dictionary<string, IPipelineStep> stepMap,
        Dictionary<string, int> order)
    {
        var byOrder = new Dictionary<int, List<StepEntry>>();
        foreach (var (name, index) in order)
        {
            if (stepMap.TryGetValue(name, out var step))
            {
                if (!byOrder.ContainsKey(index))
                    byOrder[index] = [];
                byOrder[index].Add(new StepEntry(step.Name, step.ProcessAsync));
            }
        }
        return byOrder
            .OrderBy(kv => kv.Key)
            .Select(kv => new StepGroup(false, kv.Value))
            .ToList();
    }

    private async Task<MessageContext> RunSequentialGroupAsync(
        MessageContext context, IReadOnlyList<StepEntry> steps)
    {
        foreach (var step in steps)
        {
            if (ShouldBreak(context)) break;

            var sw = Stopwatch.StartNew();
            try
            {
                context = await step.Execute(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipelineRunner: step '{Name}' threw", step.Name);
                context.PipelineError = ex.Message;
                break;
            }
            sw.Stop();
            _logger.LogDebug("PipelineRunner: step '{Name}' completed in {ElapsedMs}ms",
                step.Name, sw.ElapsedMilliseconds);
        }
        return context;
    }

    private async Task<MessageContext> RunParallelGroupAsync(
        MessageContext context, IReadOnlyList<StepEntry> steps)
    {
        var sw = Stopwatch.StartNew();
        var tasks = new Task[steps.Count];
        var errors = new ConcurrentBag<(string Name, string Error)>();

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await step.Execute(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PipelineRunner: parallel step '{Name}' threw", step.Name);
                    errors.Add((step.Name, ex.Message));
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        if (errors.Count > 0)
        {
            context.PipelineError = string.Join("; ", errors.Select(e => $"{e.Name}: {e.Error}"));
            _logger.LogError("PipelineRunner: parallel group had {Count} error(s): {Errors}",
                errors.Count, context.PipelineError);
        }

        _logger.LogDebug("PipelineRunner: parallel group completed in {ElapsedMs}ms",
            sw.ElapsedMilliseconds);
        return context;
    }

    private static bool ShouldBreak(MessageContext context)
    {
        return context.SafetyBlocked
            || context.GrammarCheckBlocked
            || context.AntiPatternBlocked
            || context.QualityGateBlocked
            || context.DoDBlocked;
    }
}
