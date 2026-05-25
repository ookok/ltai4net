using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== GEPA 风格提示词优化器 ====================

public record PromptCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Domain { get; init; } = "";
    public string Prompt { get; init; } = "";
    public List<string> Ancestors { get; init; } = new();  // 祖先 ID 列表
    public List<string> Lessons { get; init; } = new();    // 积累的教训
    public float Accuracy { get; init; }
    public float Diversity { get; init; }  // 多样性分数
    public int EvalCount { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsParetoOptimal { get; init; }
}

public record ActionableSideInfo
{
    public List<string> ErrorMessages { get; init; } = new();
    public List<string> ReasoningLogs { get; init; } = new();
    public List<string> ToolCallTraces { get; init; } = new();
    public List<string> SuccessPatterns { get; init; } = new();
    public List<string> FailurePatterns { get; init; } = new();
    public float ScalarReward { get; init; }
    public string? Diagnosis { get; init; }
    public string? Suggestion { get; init; }
}

public record GEPAConfig
{
    public int PopulationSize { get; init; } = 5;  // Pareto 前沿大小
    public int MaxReflections { get; init; } = 3;  // 最大反思次数
    public float DiversityThreshold { get; init; } = 0.3f;  // 多样性阈值
    public bool EnableMerge { get; init; } = true;  // 启用系统感知合并
    public int MaxCandidatesPerCycle { get; init; } = 10;  // 每周期最大候选数
}

public sealed class GEPAPromptOptimizer
{
    private readonly Dictionary<string, List<PromptCandidate>> _paretoFrontiers = new();
    private readonly ILogger<GEPAPromptOptimizer> _logger;
    private readonly GEPAConfig _config;
    private readonly object _lock = new();
    private int _totalOptimizations;
    private int _totalReflections;

    public GEPAPromptOptimizer(
        GEPAConfig? config = null,
        ILogger<GEPAPromptOptimizer>? logger = null)
    {
        _config = config ?? new GEPAConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GEPAPromptOptimizer>.Instance;

        _logger.LogInformation(
            "GEPAPromptOptimizer initialized: population={Pop} reflections={Reflect} merge={Merge}",
            _config.PopulationSize, _config.MaxReflections, _config.EnableMerge);
    }

    // ==================== 核心优化流程 ====================

    public async Task<List<PromptCandidate>> OptimizeAsync(
        string domain,
        List<InteractionResult> interactions,
        List<PromptCandidate> existingContexts,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting GEPA optimization: domain={Domain} interactions={Count} existing={Existing}",
            domain, interactions.Count, existingContexts.Count);

        // 1. 提取可操作的侧面信息 (ASI)
        var asi = ExtractActionableSideInfo(interactions);

        // 2. 自然语言反思
        var reflections = await ReflectAsync(existingContexts, asi, ct).ConfigureAwait(false);

        // 3. 提出候选更新
        var candidates = await ProposeCandidatesAsync(existingContexts, reflections, asi, ct).ConfigureAwait(false);

        // 4. 评估候选
        var scored = await EvaluateCandidatesAsync(candidates, interactions, ct).ConfigureAwait(false);

        // 5. 更新 Pareto 前沿
        var newFrontier = UpdateParetoFrontier(domain, existingContexts, scored);

        _totalOptimizations++;

        _logger.LogInformation(
            "GEPA optimization completed: domain={Domain} candidates={Candidates} pareto={Pareto} total={Total}",
            domain, candidates.Count, newFrontier.Count, _totalOptimizations);

        return newFrontier;
    }

    // ==================== 1. 提取 ASI ====================

    private ActionableSideInfo ExtractActionableSideInfo(List<InteractionResult> interactions)
    {
        var errorMessages = new List<string>();
        var reasoningLogs = new List<string>();
        var toolCallTraces = new List<string>();
        var successPatterns = new List<string>();
        var failurePatterns = new List<string>();
        float scalarReward = 0f;

        foreach (var interaction in interactions)
        {
            if (interaction.WasSuccessful)
            {
                successPatterns.Add(ExtractPattern(interaction));
                scalarReward += interaction.Reward;
            }
            else
            {
                failurePatterns.Add(ExtractPattern(interaction));
                if (!string.IsNullOrEmpty(interaction.Error))
                {
                    errorMessages.Add(interaction.Error);
                }
            }

            reasoningLogs.AddRange(interaction.ReasoningSteps);
            toolCallTraces.AddRange(interaction.ToolCalls);
        }

        // 平均奖励
        scalarReward = interactions.Count > 0
            ? scalarReward / interactions.Count
            : 0f;

        // 诊断和建议
        var diagnosis = DiagnoseIssues(new ActionableSideInfo
        {
            ErrorMessages = errorMessages,
            SuccessPatterns = successPatterns,
            FailurePatterns = failurePatterns,
            ScalarReward = scalarReward
        });

        var suggestion = GenerateSuggestion(new ActionableSideInfo
        {
            SuccessPatterns = successPatterns,
            FailurePatterns = failurePatterns
        });

        return new ActionableSideInfo
        {
            ErrorMessages = errorMessages,
            ReasoningLogs = reasoningLogs,
            ToolCallTraces = toolCallTraces,
            SuccessPatterns = successPatterns,
            FailurePatterns = failurePatterns,
            ScalarReward = scalarReward,
            Diagnosis = diagnosis,
            Suggestion = suggestion
        };
    }

    private static string ExtractPattern(InteractionResult interaction)
    {
        var patterns = new List<string>();

        // 提取查询中的关键词
        var keywords = interaction.Query
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(3);

        patterns.AddRange(keywords);

        // 添加响应特征
        if (interaction.Response.Length > 0)
        {
            patterns.Add($"response_length={interaction.Response.Length}");
        }

        return string.Join(", ", patterns);
    }

    private static string? DiagnoseIssues(ActionableSideInfo asi)
    {
        if (asi.FailurePatterns.Count > asi.SuccessPatterns.Count * 2)
        {
            return "High failure rate detected. Consider adding more examples and clarifying instructions.";
        }

        if (asi.ErrorMessages.Count > 0)
        {
            return $"Errors detected: {string.Join("; ", asi.ErrorMessages.Take(3))}";
        }

        if (asi.ScalarReward < 0.5f)
        {
            return "Low average reward. The current prompt may not be effective.";
        }

        return null;
    }

    private static string? GenerateSuggestion(ActionableSideInfo asi)
    {
        if (asi.SuccessPatterns.Count > 0)
        {
            return $"Leverage successful patterns: {string.Join(", ", asi.SuccessPatterns.Take(3))}";
        }

        if (asi.FailurePatterns.Count > 0)
        {
            return $"Avoid failure patterns: {string.Join(", ", asi.FailurePatterns.Take(3))}";
        }

        return "Collect more interactions to identify patterns.";
    }

    // ==================== 2. 自然语言反思 ====================

    private async Task<List<string>> ReflectAsync(
        List<PromptCandidate> existingContexts,
        ActionableSideInfo asi,
        CancellationToken ct)
    {
        var reflections = new List<string>();

        // 基于 ASI 生成反思
        if (!string.IsNullOrEmpty(asi.Diagnosis))
        {
            reflections.Add($"Diagnosis: {asi.Diagnosis}");
        }

        if (!string.IsNullOrEmpty(asi.Suggestion))
        {
            reflections.Add($"Suggestion: {asi.Suggestion}");
        }

        // 分析成功/失败模式
        if (asi.SuccessPatterns.Count > 0)
        {
            reflections.Add($"Success patterns observed: {string.Join("; ", asi.SuccessPatterns.Take(5))}");
        }

        if (asi.FailurePatterns.Count > 0)
        {
            reflections.Add($"Failure patterns observed: {string.Join("; ", asi.FailurePatterns.Take(5))}");
        }

        // 从祖先继承教训
        var ancestorLessons = existingContexts
            .SelectMany(c => c.Lessons)
            .Distinct()
            .ToList();

        if (ancestorLessons.Count > 0)
        {
            reflections.Add($"Accumulated lessons from ancestors: {string.Join("; ", ancestorLessons.Take(3))}");
        }

        _totalReflections += reflections.Count;

        _logger.LogDebug(
            "Reflection completed: reflections={Count} total={Total}",
            reflections.Count, _totalReflections);

        return reflections;
    }

    // ==================== 3. 提出候选 ====================

    private async Task<List<PromptCandidate>> ProposeCandidatesAsync(
        List<PromptCandidate> existingContexts,
        List<string> reflections,
        ActionableSideInfo asi,
        CancellationToken ct)
    {
        var candidates = new List<PromptCandidate>();

        // 从现有上下文变异
        foreach (var existing in existingContexts.Take(_config.MaxCandidatesPerCycle / 2))
        {
            if (ct.IsCancellationRequested) break;

            var mutated = MutateCandidate(existing, reflections, asi);
            candidates.Add(mutated);
        }

        // 从头生成新候选
        var newCount = Math.Min(
            _config.MaxCandidatesPerCycle - candidates.Count,
            _config.MaxCandidatesPerCycle / 2);

        for (var i = 0; i < newCount; i++)
        {
            if (ct.IsCancellationRequested) break;

            var newCandidate = GenerateNewCandidate(reflections, asi);
            candidates.Add(newCandidate);
        }

        // 如果启用合并，尝试合并 Pareto 最优候选
        if (_config.EnableMerge && existingContexts.Count >= 2)
        {
            var paretoCandidates = existingContexts
                .Where(c => c.IsParetoOptimal)
                .Take(2)
                .ToList();

            if (paretoCandidates.Count == 2)
            {
                var merged = MergeCandidates(paretoCandidates[0], paretoCandidates[1], asi);
                candidates.Add(merged);
            }
        }

        _logger.LogDebug("Candidates proposed: count={Count}", candidates.Count);

        return candidates;
    }

    private PromptCandidate MutateCandidate(
        PromptCandidate existing,
        List<string> reflections,
        ActionableSideInfo asi)
    {
        var newLessons = existing.Lessons.ToList();
        newLessons.AddRange(reflections.Take(_config.MaxReflections));

        // 简单变异：添加新教训到提示词
        var mutatedPrompt = new StringBuilder(existing.Prompt);
        if (reflections.Count > 0)
        {
            mutatedPrompt.Append("\n\n## Learned Lessons:\n");
            foreach (var lesson in reflections.Take(_config.MaxReflections))
            {
                mutatedPrompt.Append("- ").Append(lesson).Append('\n');
            }
        }

        var mutatedPromptStr = mutatedPrompt.ToString();
        return new PromptCandidate
        {
            Domain = existing.Domain,
            Prompt = mutatedPromptStr,
            Ancestors = new List<string> { existing.Id },
            Lessons = newLessons.Distinct().ToList(),
            Accuracy = existing.Accuracy,  // 继承父代准确率
            Diversity = ComputeDiversity(mutatedPromptStr, existing.Prompt),
            EvalCount = 0,
            IsParetoOptimal = false
        };
    }

    private PromptCandidate GenerateNewCandidate(
        List<string> reflections,
        ActionableSideInfo asi)
    {
        var sb = new StringBuilder();
        sb.Append("You are a helpful assistant.\n\n");

        if (asi.Suggestion != null)
        {
            sb.Append("## Guidance:\n").Append(asi.Suggestion).Append("\n\n");
        }

        if (asi.SuccessPatterns.Count > 0)
        {
            sb.Append("## Successful Patterns:\n");
            foreach (var pattern in asi.SuccessPatterns.Take(3))
            {
                sb.Append("- ").Append(pattern).Append('\n');
            }
            sb.Append('\n');
        }

        if (asi.FailurePatterns.Count > 0)
        {
            sb.Append("## Avoid These Patterns:\n");
            foreach (var pattern in asi.FailurePatterns.Take(3))
            {
                sb.Append("- ").Append(pattern).Append('\n');
            }
            sb.Append('\n');
        }

        return new PromptCandidate
        {
            Prompt = sb.ToString(),
            Lessons = reflections.Take(_config.MaxReflections).ToList(),
            Accuracy = 0.5f,  // 新候选默认准确率
            Diversity = 1.0f,  // 新候选最大多样性
            EvalCount = 0,
            IsParetoOptimal = false
        };
    }

    private PromptCandidate MergeCandidates(
        PromptCandidate a,
        PromptCandidate b,
        ActionableSideInfo asi)
    {
        // 合并两个候选的优势
        var mergedLessons = a.Lessons.Union(b.Lessons).Distinct().ToList();
        var mergedAncestors = new List<string> { a.Id, b.Id };

        var mergedSb = new StringBuilder();
        mergedSb.Append("# Combined Strategy\n\n");
        mergedSb.Append("## From Candidate A:\n").Append(a.Prompt).Append("\n\n");
        mergedSb.Append("## From Candidate B:\n").Append(b.Prompt).Append("\n\n");
        mergedSb.Append("## Combined Lessons:\n");
        foreach (var lesson in mergedLessons.Take(5))
        {
            mergedSb.Append("- ").Append(lesson).Append('\n');
        }

        return new PromptCandidate
        {
            Prompt = mergedSb.ToString(),
            Ancestors = mergedAncestors,
            Lessons = mergedLessons,
            Accuracy = Math.Max(a.Accuracy, b.Accuracy),
            Diversity = ComputeDiversity(a.Prompt, b.Prompt),
            EvalCount = 0,
            IsParetoOptimal = false
        };
    }

    // ==================== 4. 评估候选 ====================

    private async Task<List<PromptCandidate>> EvaluateCandidatesAsync(
        List<PromptCandidate> candidates,
        List<InteractionResult> interactions,
        CancellationToken ct)
    {
        var scored = new List<PromptCandidate>();

        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // 使用候选处理交互子集进行评估
            var testInteractions = interactions.Take(10).ToList();
            var successCount = 0;

            foreach (var interaction in testInteractions)
            {
                // 简单评估：检查候选提示词是否包含成功模式
                var containsSuccessPattern = candidate.Prompt.Contains(
                    interaction.Query.Split(' ').FirstOrDefault() ?? "",
                    StringComparison.OrdinalIgnoreCase);

                if (containsSuccessPattern || interaction.WasSuccessful)
                {
                    successCount++;
                }
            }

            var accuracy = testInteractions.Count > 0
                ? (float)successCount / testInteractions.Count
                : 0.5f;

            scored.Add(candidate with
            {
                Accuracy = accuracy,
                EvalCount = candidate.EvalCount + 1
            });
        }

        return scored;
    }

    // ==================== 5. 更新 Pareto 前沿 ====================

    private List<PromptCandidate> UpdateParetoFrontier(
        string domain,
        List<PromptCandidate> existing,
        List<PromptCandidate> scored)
    {
        lock (_lock)
        {
            // 合并现有和新的候选
            var allCandidates = existing.Concat(scored).ToList();

            // 计算 Pareto 前沿（准确率 vs 多样性）
            var paretoFrontier = ComputeParetoFrontier(allCandidates);

            // 限制前沿大小
            if (paretoFrontier.Count > _config.PopulationSize)
            {
                paretoFrontier = paretoFrontier
                    .OrderByDescending(c => c.Accuracy * 0.7f + c.Diversity * 0.3f)
                    .Take(_config.PopulationSize)
                    .ToList();
            }

            // 标记 Pareto 最优
            var paretoIds = paretoFrontier.Select(c => c.Id).ToHashSet();
            paretoFrontier = paretoFrontier
                .Select(c => c with { IsParetoOptimal = true })
                .ToList();

            // 更新存储
            _paretoFrontiers[domain] = paretoFrontier;

            return paretoFrontier;
        }
    }

    private List<PromptCandidate> ComputeParetoFrontier(List<PromptCandidate> candidates)
    {
        var pareto = new List<PromptCandidate>();

        foreach (var candidate in candidates)
        {
            var isDominated = false;

            foreach (var other in candidates)
            {
                if (candidate.Id == other.Id) continue;

                // 检查是否被支配
                if (other.Accuracy >= candidate.Accuracy &&
                    other.Diversity >= candidate.Diversity &&
                    (other.Accuracy > candidate.Accuracy || other.Diversity > candidate.Diversity))
                {
                    isDominated = true;
                    break;
                }
            }

            if (!isDominated)
            {
                pareto.Add(candidate);
            }
        }

        return pareto;
    }

    // ==================== 辅助方法 ====================

    private static float ComputeDiversity(string promptA, string promptB)
    {
        var wordsA = promptA.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = promptB.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? 1.0f - (float)intersection / union : 1.0f;
    }

    // ==================== 公开查询方法 ====================

    public List<PromptCandidate> GetParetoFrontier(string domain)
    {
        lock (_lock)
        {
            return _paretoFrontiers.GetValueOrDefault(domain, new List<PromptCandidate>());
        }
    }

    public GEPAStats GetStats()
    {
        lock (_lock)
        {
            return new GEPAStats
            {
                TotalOptimizations = _totalOptimizations,
                TotalReflections = _totalReflections,
                DomainCount = _paretoFrontiers.Count,
                ParetoFrontiers = _paretoFrontiers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        Count = kvp.Value.Count,
                        AvgAccuracy = kvp.Value.Count > 0 ? kvp.Value.Average(c => c.Accuracy) : 0f,
                        AvgDiversity = kvp.Value.Count > 0 ? kvp.Value.Average(c => c.Diversity) : 0f
                    })
            };
        }
    }
}

public sealed record GEPAStats
{
    public int TotalOptimizations { get; init; }
    public int TotalReflections { get; init; }
    public int DomainCount { get; init; }
    public object ParetoFrontiers { get; init; } = new();
}
