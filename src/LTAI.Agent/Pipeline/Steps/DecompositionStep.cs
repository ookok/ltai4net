using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public enum QueryComplexity { Low, Medium, High }

public sealed class DecompositionStep : IPipelineStep
{
    private readonly IChatClient _llm;
    private readonly PlanLearningStore? _planStore;
    private readonly ILogger<DecompositionStep> _logger;

    public string Name => "Decomposition";

    public DecompositionStep(
        IChatClient llm,
        PlanLearningStore? planStore = null,
        ILogger<DecompositionStep>? logger = null)
    {
        _llm = llm;
        _planStore = planStore;
        _logger = logger ?? NullLogger<DecompositionStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (context.TryGet<List<string>>("Decomposition", out _))
        {
            _logger.LogDebug("DecompositionStep: already decomposed, skipping");
            return context;
        }

        var query = context.Request;
        if (string.IsNullOrWhiteSpace(query))
            return context;

        // ── Adaptive complexity check ──
        var complexity = EstimateComplexity(query);
        context.Set("_QueryComplexity", complexity);

        if (complexity == QueryComplexity.Low)
        {
            context.Set("Decomposition", new List<string> { query });
            context.Set("SkillWeaverFastPath", true);
            _logger.LogInformation("DecompositionStep: low complexity, fast path");
            return context;
        }

        // ── Cross-session plan lookup ──
        if (_planStore != null)
        {
            var storedPlan = await _planStore.FindSimilarAsync(query, context.CancellationToken).ConfigureAwait(false);
            if (storedPlan != null)
            {
                var tasks = storedPlan.SubTasks.Select(p => p.Description).ToList();
                context.Set("Decomposition", tasks);
                context.Set("CompositionPlan", storedPlan);
                var preSelectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in storedPlan.SubTasks)
                    if (p.AssignedTool != null)
                        preSelectedNames.Add(p.AssignedTool);
                context.Set("_PreSelectedToolNames", preSelectedNames);
                _logger.LogInformation("DecompositionStep: cross-session plan match, {Count} sub-tasks", tasks.Count);
                if (tasks.Count <= 2)
                    context.Set("SkillWeaverFastPath", true);

                var planText = CompositionStep.BuildPlanTextStatic(storedPlan);
                lock (context.MessagesLock)
                    context.Messages.Add(new ChatMessage(ChatRole.System, planText));
                return context;
            }
        }

        // ── Skip if a plan already exists (e.g. from DynamicReplanStep or cross-session lookup) ──
        if (context.TryGet<CompositionPlan>("CompositionPlan", out _))
        {
            _logger.LogDebug("DecompositionStep: plan already exists from DynamicReplan or cache, skipping");
            return context;
        }

        // ── Cache lookup: skip LLM call if we have a cached decomposition ──
        if (DecompositionCache.TryGet(query, out var cachedTasks))
        {
            context.Set("Decomposition", cachedTasks);
            _logger.LogInformation("DecompositionStep: cache hit, {Count} sub-tasks", cachedTasks.Count);
            if (cachedTasks.Count <= 2)
                context.Set("SkillWeaverFastPath", true);
            return context;
        }

        var maxTokens = complexity == QueryComplexity.High ? 1024 : 512;
        var prompt = $"""
            将用户请求分解为原子子任务。约束：
            - 每个子任务恰好需要1个工具或技能完成
            - 子任务之间不重叠
            - 按执行顺序输出
            - 只输出JSON数组，例如：["子任务1","子任务2","子任务3"]

            用户请求：{query}
            """;

        try
        {
            var response = await _llm.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = maxTokens },
                context.CancellationToken).ConfigureAwait(false);

            var text = response.Text ?? "";
            var tasks = ParseDecomposition(text);

            if (tasks.Count > 0)
            {
                context.Set("Decomposition", tasks);
                DecompositionCache.Set(query, tasks);
                _logger.LogInformation("DecompositionStep: {Count} sub-tasks (complexity={Complexity}): {Tasks}",
                    tasks.Count, complexity, string.Join(" | ", tasks));
            }
            else
            {
                context.Set("Decomposition", new List<string> { query });
                _logger.LogWarning("DecompositionStep: parse failed, using whole query as single task");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DecompositionStep failed");
            context.Set("Decomposition", new List<string> { query });
        }

        // ── Fast path: <=2 sub-tasks → skip SADFeedback + Composition ──
        if (context.TryGet<List<string>>("Decomposition", out var tasks2) && tasks2.Count <= 2)
            context.Set("SkillWeaverFastPath", true);

        return context;
    }

    internal static QueryComplexity EstimateComplexity(string query)
    {
        var q = query.Trim();
        if (q.Length <= 10)
            return QueryComplexity.Low;

        // Multiple action-related words → higher complexity
        var actionWords = new[] { "and", "then", "之后", "然后", "并且", "同时", "分别", "repos", "直接" };
        var actionCount = 0;
        var lower = q.ToLowerInvariant();
        foreach (var w in actionWords)
        {
            var idx = lower.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                actionCount++;
                idx = lower.IndexOf(w, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (q.Length >= 80 && actionCount >= 2)
            return QueryComplexity.High;

        if (q.Length >= 40 || actionCount >= 1)
            return QueryComplexity.Medium;

        return QueryComplexity.Low;
    }

    internal static List<string> ParseDecomposition(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        try
        {
            var json = text[start..(end + 1)];
            var arr = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (arr is { Count: > 0 } && arr.All(s => !string.IsNullOrWhiteSpace(s)))
                return [.. arr.Select(s => s.Trim())];
        }
        catch { }

        return [];
    }
}
