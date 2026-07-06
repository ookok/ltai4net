using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class SADFeedbackStep : IPipelineStep
{
    private const int HintCount = 15;

    private readonly IChatClient _llm;
    private readonly IToolRegistry? _toolRegistry;
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<SADFeedbackStep> _logger;

    public string Name => "SADFeedback";

    public SADFeedbackStep(
        IChatClient llm,
        IToolRegistry? toolRegistry = null,
        EmbeddingClient? embedder = null,
        ILogger<SADFeedbackStep>? logger = null)
    {
        _llm = llm;
        _toolRegistry = toolRegistry;
        _embedder = embedder;
        _logger = logger ?? NullLogger<SADFeedbackStep>.Instance;
    }

    private const int MaxCoverageIterations = 2;

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // ── Skip if a plan already exists (e.g. from DynamicReplanStep or cross-session lookup) ──
        if (context.TryGet<CompositionPlan>("CompositionPlan", out _))
        {
            _logger.LogDebug("SADFeedbackStep: plan already exists, skipping");
            return context;
        }

        // ── Fast path: skip refinement for simple queries ──
        if (context.TryGet<bool>("SkillWeaverFastPath", out var fast) && fast)
        {
            _logger.LogDebug("SADFeedbackStep: fast path, skipping refinement");
            if (context.TryGet<List<string>>("Decomposition", out var ft))
                context.Set("_FinalDecomposition", ft);
            return context;
        }

        if (!context.TryGet<List<string>>("Decomposition", out var tasks) || tasks is not { Count: > 0 })
        {
            _logger.LogDebug("SADFeedbackStep: no decomposition found, skipping");
            return context;
        }

        if (context.TryGet<List<string>>("_FinalDecomposition", out _))
        {
            _logger.LogDebug("SADFeedbackStep: already refined, skipping");
            return context;
        }

        var query = context.Request;
        if (string.IsNullOrWhiteSpace(query)) return context;

        List<string> refinedTasks;
        bool canRetrieve = _toolRegistry is { IsInitialized: true } && _embedder != null;

        if (canRetrieve)
        {
            refinedTasks = await RefineWithHintsAsync(tasks, query, context.CancellationToken).ConfigureAwait(false);

            // ── SAD coverage verification loop: check each sub-task has a matching tool ──
            for (int iter = 0; iter < MaxCoverageIterations; iter++)
            {
                var uncovered = new List<(int Index, string Task)>();
                for (int i = 0; i < refinedTasks.Count; i++)
                {
                    var hits = await _toolRegistry!.SearchTopKAsync(refinedTasks[i], _embedder!, null,
                        k: 1, context.CancellationToken).ConfigureAwait(false);
                    if (hits.Count == 0)
                        uncovered.Add((i, refinedTasks[i]));
                }

                if (uncovered.Count == 0)
                    break; // all covered

                _logger.LogInformation("SADFeedbackStep: coverage iteration {Iter}, {Count} uncovered sub-tasks",
                    iter + 1, uncovered.Count);

                var uncoveredText = string.Join("\n", uncovered.Select(u => $"  [{u.Index}] {u.Task}"));
                var coverPrompt = $"""
                    以下子任务在当前可用工具集中没有匹配的工具。请重新分解：
                    - 将没有匹配的子任务合并到其他子任务中
                    - 或改写子任务使其能使用现有工具

                    用户请求：{query}

                    当前子任务：
                    {string.Join("\n", refinedTasks.Select((t, i) => $"  [{i}] {t}"))}

                    没有匹配的子任务：
                    {uncoveredText}

                    输出合并/改写后的完整JSON数组，例如：["子任务1","子任务2","子任务3"]
                    """;

                try
                {
                    var response = await _llm.GetResponseAsync(
                        [new ChatMessage(ChatRole.User, coverPrompt)],
                        new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 512 },
                        context.CancellationToken).ConfigureAwait(false);

                    var text = response.Text ?? "";
                    var newTasks = DecompositionStep.ParseDecomposition(text);
                    if (newTasks.Count > 0)
                        refinedTasks = newTasks;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SADFeedbackStep: coverage repair iteration {Iter} failed", iter + 1);
                }
            }

            _logger.LogInformation("SADFeedbackStep: final {Count} sub-tasks after coverage verification",
                refinedTasks.Count);
        }
        else
        {
            _logger.LogDebug("SADFeedbackStep: ToolRegistry not ready, using vanilla decomposition");
            refinedTasks = tasks;
        }

        context.Set("_FinalDecomposition", refinedTasks);
        return context;
    }

    /// <summary>
    /// Retrieve tool hints and ask LLM to re-decompose the query.
    /// </summary>
    private async Task<List<string>> RefineWithHintsAsync(
        List<string> tasks, string query, CancellationToken ct)
    {
        var allHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            try
            {
                var hits = await _toolRegistry!.SearchTopKAsync(task, _embedder!, null,
                    HintCount, ct).ConfigureAwait(false);
                foreach (var hit in hits.Take(HintCount))
                    if (hit.Name != null) allHints.Add(hit.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SADFeedbackStep: search failed for '{Task}'", task);
            }
        }

        var hints = allHints.Take(HintCount).ToList();
        _logger.LogInformation("SADFeedbackStep: retrieved {Count} hint skills for re-decomposition", hints.Count);

        if (hints.Count == 0)
            return tasks;

        var hintText = string.Join("\n", hints.Select(h => $"  - {h}"));
        var prompt = $"""
            基于以下可用的技能重新分解用户请求。

            用户请求：{query}

            可用技能：
            {hintText}

            请将请求分解为原子子任务，每个子任务应对应上面1个技能。
            按执行顺序输出JSON数组，例如：["子任务1","子任务2","子任务3"]
            """;

        try
        {
            var response = await _llm.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 512 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            var refined = DecompositionStep.ParseDecomposition(text);
            if (refined.Count > 0)
            {
                _logger.LogInformation("SADFeedbackStep: refined {Before}→{After} sub-tasks",
                    tasks.Count, refined.Count);
                return refined;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SADFeedbackStep: re-decomposition LLM call failed");
        }

        return tasks;
    }
}
