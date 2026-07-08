using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Pipeline;

public sealed class PipelineRunner
{
    private readonly IReadOnlyList<StepGroup> _preGroups;
    private readonly IReadOnlyList<StepGroup> _postGroups;
    private readonly ILogger<PipelineRunner> _logger;

    /// <summary>Default pre-generation step order (name → execution index).
    /// Steps with the same index run sequentially in definition order.</summary>
    internal static readonly Dictionary<string, int> DefaultPreStepOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MetaSkillInjector"] = 0,
        ["MultiTrajectoryRollout"] = 1,
        ["DynamicReplan"] = 2,
        ["MemoryCaching(Restore)"] = 4,
        ["RagContext"] = 5,
        ["ReflectionAugmented"] = 6,
        ["ProgressGuard"] = 7,
        ["GenerationOrder"] = 8,
        ["Decomposition"] = 9,
        ["SADFeedback"] = 10,
        ["Composition"] = 11,
        ["ProactiveSuggest"] = 12,
        ["SafetyCheck"] = 13,
        ["ToolExecution"] = 15,
    };

    /// <summary>
    /// Default post-generation step plan. Each entry defines a group:
    ///   order: execution sequence (lower runs first)
    ///   parallel: if true, all steps in the group run concurrently
    ///   alwaysRun: if true, runs even when pipeline is blocked (for critic-repair synthesis)
    ///   names: step names in this group
    /// </summary>
    internal static readonly (int Order, bool Parallel, bool AlwaysRun, string[] Names)[] DefaultPostStepPlan = [
        (0,  false, false, ["DeltaAnchor"]),
        (1,  false, false, ["MemoryCaching(Save)"]),
        (2,  false, false, ["Compaction"]),
        (3,  false, false, ["DiscoursePlanning"]),
        // GrammarCheck runs first (sequentially) because QualityGate and DoDCheck read
        // GrammarCheckBlocked / "GrammarErrors" set by it. Running them in the same parallel
        // group caused a timing race that silently dropped syntax-error detection.
        (4,  false, false, ["GrammarCheck"]),
        (5,  true,  false, ["AntiPatternCheck", "AntiPatternPatch", "QualityGate", "DoDCheck", "ThinkingTag", "AbstentionCheck", "ToolEval", "PlanVerification"]),
        (6,  false, true,  ["SelfRefine"]),
        (7,  false, true,  ["CriticRepair"]),
        (8,  false, true,  ["SelfReflection"]),
        (9,  false, false, ["Retrospective"]),
    ];

    private sealed record StepEntry(
        string Name,
        Func<MessageContext, Task<MessageContext>> Execute);

    private sealed record StepGroup(
        bool IsParallel,
        bool AlwaysRun,
        IReadOnlyList<StepEntry> Steps);

    /// <summary>
    /// DI-friendly constructor: collects all registered <see cref="IPipelineStep"/>
    /// instances and orders them by the configured or default step plan.
    /// Steps not in the plan are skipped. Configure via <c>LTAI:Pipeline</c> section
    /// in appsettings.json.
    /// </summary>
    public PipelineRunner(
        IEnumerable<IPipelineStep> steps,
        ILogger<PipelineRunner>? logger = null,
        IOptions<LTAIOptions>? options = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRunner>.Instance;

        var stepMap = steps.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var cfg = options?.Value?.Pipeline;
        var preOrder = BuildPreOrder(cfg);
        var postPlan = BuildPostPlan(cfg);

        _preGroups = BuildSequentialGroups(stepMap, preOrder);
        _postGroups = postPlan
            .Select(plan =>
            {
                var stepEntries = plan.Names
                    .Select(n => stepMap.GetValueOrDefault(n))
                    .Where(s => s != null)
                    .Select(s => new StepEntry(s.Name, s.ProcessAsync))
                    .ToList();
                return stepEntries.Count > 0
                    ? new StepGroup(plan.Parallel, plan.AlwaysRun, stepEntries)
                    : null;
            })
            .Where(g => g != null)
            .ToList()!;
    }

    internal static Dictionary<string, int> BuildPreOrder(PipelineConfig? config)
    {
        if (config?.PreSteps is { Length: > 0 })
            return config.PreSteps.ToDictionary(e => e.Name, e => e.Order, StringComparer.OrdinalIgnoreCase);
        return DefaultPreStepOrder;
    }

    internal static (int Order, bool Parallel, bool AlwaysRun, string[] Names)[] BuildPostPlan(PipelineConfig? config)
    {
        if (config?.PostSteps is { Length: > 0 })
            return config.PostSteps.Select(g => (g.Order, g.Parallel, g.AlwaysRun, g.Names)).ToArray();
        return DefaultPostStepPlan;
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
            // Once the pipeline is blocked, normal post-processing groups are skipped.
            // AlwaysRun groups (SelfRefine / CriticRepair / SelfReflection) still execute so
            // they can auto-repair or learn from the blocked response instead of being skipped.
            if (!group.AlwaysRun && ShouldBreak(context))
                continue;

            context = group.IsParallel
                ? await RunParallelGroupAsync(context, group.Steps).ConfigureAwait(false)
                : await RunSequentialGroupAsync(context, group.Steps, group.AlwaysRun).ConfigureAwait(false);
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
            .Select(kv => new StepGroup(false, false, kv.Value))
            .ToList();
    }

    private async Task<MessageContext> RunSequentialGroupAsync(
        MessageContext context, IReadOnlyList<StepEntry> steps, bool ignoreBreak = false)
    {
        foreach (var step in steps)
        {
            if (!ignoreBreak && ShouldBreak(context)) break;

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

    private static readonly object _pipelineErrorLock = new();

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
            var msg = string.Join("; ", errors.Select(e => $"{e.Name}: {e.Error}"));
            lock (_pipelineErrorLock)
            {
                if (context.PipelineError == null)
                    context.PipelineError = msg;
                else
                    context.PipelineError += "; " + msg;
            }
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
            || context.DoDBlocked
            || context.AbstentionBlocked;
    }
}
