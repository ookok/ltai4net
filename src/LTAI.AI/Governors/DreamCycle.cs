using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record DreamCycleResult
{
    public int MemoriesConsolidated { get; init; }
    public int MemoriesForgotten { get; init; }
    public int PatternsMerged { get; init; }
    public int KnowledgeDistilled { get; init; }
    public int ReflectionsGenerated { get; init; }
    public string Summary { get; init; } = "";
}

// ==================== 反思触发配置 ====================

public record ReflectionTriggerConfig
{
    public float ImportanceThreshold { get; init; } = 150.0f;  // 累积重要性阈值 (论文默认值)
    public TimeSpan MinIntervalBetweenReflections { get; init; } = TimeSpan.FromHours(4);  // 最小反思间隔
    public int MaxRecentMemoriesForReflection { get; init; } = 100;  // 反思时考虑的最大近期记忆数
    public bool EnableThresholdTrigger { get; init; } = true;  // 启用阈值触发
    public bool EnableTimerFallback { get; init; } = true;  // 定时器回退
}

public record ReflectionTrigger
{
    public DateTime TriggeredAt { get; init; } = DateTime.UtcNow;
    public float AccumulatedImportance { get; init; }
    public int MemoryCount { get; init; }
    public string TriggerReason { get; init; } = "";  // "threshold", "timer", "forced"
}

public sealed class DreamCycle : BackgroundService
{
    private readonly SynapticMemory _memory;
    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly SkillTree _skillTree;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly DualMemoryStore _dualMemoryStore;
    private readonly IncrementalRuleExtractor _ruleExtractor;
    private readonly ILogger<DreamCycle> _logger;
    private readonly TimeSpan _cycleInterval;
    private readonly ReflectionTriggerConfig _reflectionConfig;
    private readonly string _dreamLogPath;
    private DateTime _lastInteraction = DateTime.UtcNow;
    private DateTime _lastReflection = DateTime.MinValue;
    private float _accumulatedImportance;
    private int _totalDreams;
    private int _totalReflections;
    private readonly List<ReflectionTrigger> _reflectionHistory = new();
    private readonly object _reflectionLock = new();

    public DreamCycle(
        SynapticMemory memory,
        KnowledgeGraphBridge graphBridge,
        SkillTree skillTree,
        MetaCognitiveLayer metaCognition,
        DualMemoryStore dualMemoryStore,
        IncrementalRuleExtractor ruleExtractor,
        ILogger<DreamCycle>? logger = null,
        TimeSpan? cycleInterval = null,
        ReflectionTriggerConfig? reflectionConfig = null)
    {
        _memory = memory;
        _graphBridge = graphBridge;
        _skillTree = skillTree;
        _metaCognition = metaCognition;
        _dualMemoryStore = dualMemoryStore;
        _ruleExtractor = ruleExtractor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DreamCycle>.Instance;
        _cycleInterval = cycleInterval ?? TimeSpan.FromMinutes(10);
        _reflectionConfig = reflectionConfig ?? new ReflectionTriggerConfig();
        _dreamLogPath = Path.Combine(AppContext.BaseDirectory, "synaptic", "dream_log.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_dreamLogPath)!);

        _logger.LogInformation(
            "DreamCycle initialized: interval={Interval}s, reflectionThreshold={Threshold}, gated={Gated}",
            _cycleInterval.TotalSeconds,
            _reflectionConfig.ImportanceThreshold,
            _reflectionConfig.EnableThresholdTrigger);
    }

    public void RecordInteraction(float importanceScore = 1.0f)
    {
        _lastInteraction = DateTime.UtcNow;

        // 累积重要性评分
        lock (_reflectionLock)
        {
            _accumulatedImportance += importanceScore;
        }
    }

    /// <summary>
    /// 检查是否应该触发反思 (基于重要性阈值)
    /// </summary>
    public bool ShouldTriggerReflection(out string reason)
    {
        reason = "";

        if (!_reflectionConfig.EnableThresholdTrigger)
        {
            if (_reflectionConfig.EnableTimerFallback)
            {
                var timeSinceLastReflection = DateTime.UtcNow - _lastReflection;
                if (timeSinceLastReflection >= _reflectionConfig.MinIntervalBetweenReflections)
                {
                    reason = "timer";
                    return true;
                }
            }
            return false;
        }

        // 检查重要性阈值
        lock (_reflectionLock)
        {
            if (_accumulatedImportance >= _reflectionConfig.ImportanceThreshold)
            {
                // 检查最小间隔
                var timeSinceLastReflection = DateTime.UtcNow - _lastReflection;
                if (timeSinceLastReflection >= _reflectionConfig.MinIntervalBetweenReflections)
                {
                    reason = "threshold";
                    return true;
                }
            }
        }

        // 定时器回退
        if (_reflectionConfig.EnableTimerFallback)
        {
            var timeSinceLastReflection = DateTime.UtcNow - _lastReflection;
            if (timeSinceLastReflection >= _reflectionConfig.MinIntervalBetweenReflections * 2)
            {
                reason = "timer_fallback";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取当前累积重要性
    /// </summary>
    public float GetAccumulatedImportance()
    {
        lock (_reflectionLock)
        {
            return _accumulatedImportance;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DreamCycle started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var idleDuration = DateTime.UtcNow - _lastInteraction;
                if (idleDuration >= _cycleInterval)
                {
                    await DreamAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dream cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DreamAsync(CancellationToken ct)
    {
        _logger.LogInformation("Dream cycle #{Count} beginning...", _totalDreams + 1);

        // 1. 双记忆系统门控整合 (Gate Consolidation)
        var consolidationResult = await ConsolidateDualMemoryAsync(ct).ConfigureAwait(false);

        // 2. 检查是否触发反思 (Reflection Trigger)
        var reflectionsGenerated = 0;
        if (ShouldTriggerReflection(out var triggerReason))
        {
            reflectionsGenerated = await GenerateReflectionsAsync(ct, triggerReason).ConfigureAwait(false);
        }

        // 3. 传统记忆维护 (Legacy Maintenance - optional, keep for SynapticMemory compatibility)
        var forgotten = ForgetWeakMemories();
        var distilled = DistillKnowledgeToGraph();

        _totalDreams++;

        var result = new DreamCycleResult
        {
            MemoriesConsolidated = consolidationResult.ExtractedLessons,
            MemoriesForgotten = forgotten,
            PatternsMerged = consolidationResult.Success ? 1 : 0, // Simplified metric
            KnowledgeDistilled = distilled,
            ReflectionsGenerated = reflectionsGenerated,
            Summary = $"Dream #{_totalDreams}: consolidated={consolidationResult.ExtractedLessons}, reflections={reflectionsGenerated}, forgotten={forgotten}"
        };

        await PersistDreamResultAsync(result, ct).ConfigureAwait(false);

        _logger.LogInformation("Dream cycle #{Count} complete: {Summary}", _totalDreams, result.Summary);
    }

    /// <summary>
    /// 执行双记忆系统的门控整合
    /// </summary>
    private async Task<ConsolidationResult> ConsolidateDualMemoryAsync(CancellationToken ct)
    {
        if (!_dualMemoryStore.ShouldConsolidate())
        {
            _logger.LogDebug("DualMemoryStore consolidation skipped (gated)");
            return new ConsolidationResult { Success = false, Reason = "Gated" };
        }

        _logger.LogInformation("Starting DualMemoryStore consolidation...");

        try
        {
            // 使用增量提取器生成规则 Delta
            var result = await _dualMemoryStore.ConsolidateIfNeededAsync(
                async (episodes) =>
                {
                    var deltas = await _ruleExtractor.ExtractDeltasAsync(episodes, ct).ConfigureAwait(false);
                    // 将 Delta 转换为 AbstractLesson
                    return deltas.Select(d => new AbstractLesson
                    {
                        Title = d.Title,
                        Kind = LessonKind.Rule, // Default kind
                        Content = d.Content,
                        Domain = d.Domain,
                        SourceEpisodeIds = d.SourceEpisodeIds,
                        HelpfulCount = d.HelpfulCount,
                        HarmfulCount = d.HarmfulCount,
                        QualityScore = d.Confidence,
                        Version = 1
                    }).ToList();
                },
                ct);

            if (result.Success)
            {
                _logger.LogInformation(
                    "DualMemory consolidation success: extracted={Extracted}, qualified={Qualified}",
                    result.ExtractedLessons, result.QualifiedLessons);
            }
            else
            {
                _logger.LogWarning("DualMemory consolidation failed/skipped: {Reason}", result.Reason);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DualMemory consolidation failed with exception");
            return new ConsolidationResult { Success = false, Reason = ex.Message };
        }
    }

    /// <summary>
    /// 生成反思 (Generative Agents 论文机制)
    /// 当累积重要性超过阈值时，从近期记忆中提取高阶洞察
    /// </summary>
    private async Task<int> GenerateReflectionsAsync(CancellationToken ct, string triggerReason)
    {
        var reflectionsGenerated = 0;

        try
        {
            // 获取近期记忆用于反思
            var recentExperiences = _memory.GetRecentUntrained(_reflectionConfig.MaxRecentMemoriesForReflection);
            if (recentExperiences.Count < 10)
            {
                _logger.LogDebug("Insufficient memories for reflection: {Count}", recentExperiences.Count);
                return 0;
            }

            // 生成反思问题 (模拟论文中的 LLM 提示)
            var reflectionQuestions = GenerateReflectionQuestions(recentExperiences);

            // 为每个问题生成反思洞察
            foreach (var question in reflectionQuestions.Take(3))
            {
                if (ct.IsCancellationRequested) break;

                var relevantMemories = recentExperiences
                    .Where(e => e.Query.Contains(question, StringComparison.OrdinalIgnoreCase))
                    .Take(10)
                    .ToList();

                if (relevantMemories.Count >= 3)
                {
                    var insight = SynthesizeInsight(question, relevantMemories);
                    _logger.LogInformation(
                        "Reflection generated: question='{Question}' insight='{Insight}' source_count={Count}",
                        question, insight[..Math.Min(50, insight.Length)], relevantMemories.Count);

                    reflectionsGenerated++;
                }
            }

            // 记录反思触发
            var trigger = new ReflectionTrigger
            {
                TriggeredAt = DateTime.UtcNow,
                AccumulatedImportance = _accumulatedImportance,
                MemoryCount = recentExperiences.Count,
                TriggerReason = triggerReason
            };

            lock (_reflectionLock)
            {
                _reflectionHistory.Add(trigger);
                if (_reflectionHistory.Count > 100)
                {
                    _reflectionHistory.RemoveRange(0, _reflectionHistory.Count - 50);
                }

                // 重置累积重要性
                _accumulatedImportance = 0;
                _lastReflection = DateTime.UtcNow;
                _totalReflections++;
            }

            _logger.LogInformation(
                "Reflection cycle completed: generated={Generated} reason={Reason} total={Total}",
                reflectionsGenerated, triggerReason, _totalReflections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reflection generation failed");
        }

        return reflectionsGenerated;
    }

    /// <summary>
    /// 从近期记忆生成反思问题 (模拟论文 LLM 提示)
    /// </summary>
    private static List<string> GenerateReflectionQuestions(List<SynapticExperience> experiences)
    {
        var questions = new List<string>();

        // 提取高频主题
        var topKeywords = experiences
            .SelectMany(e => e.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 3)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        if (topKeywords.Count > 0)
        {
            questions.Add($"What is the agent's relationship with {topKeywords[0]}?");
        }

        // 提取成功/失败模式
        var successRate = experiences.Count(e => e.Reward > 0.7f) / (float)Math.Max(1, experiences.Count);
        if (successRate < 0.5f)
        {
            questions.Add("Why is the agent struggling with recent tasks?");
        }
        else
        {
            questions.Add("What strategies is the agent using successfully?");
        }

        // 提取关系模式
        var uniqueLabels = experiences.Select(e => e.Label).Distinct().Count();
        if (uniqueLabels > 3)
        {
            questions.Add("What patterns exist across different domains?");
        }

        return questions;
    }

    /// <summary>
    /// 综合洞察 (模拟论文中的 LLM 推理)
    /// </summary>
    private static string SynthesizeInsight(string question, List<SynapticExperience> relevantMemories)
    {
        var commonPatterns = relevantMemories
            .GroupBy(e => e.Response[..Math.Min(30, e.Response.Length)])
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (commonPatterns != null)
        {
            return $"Based on {relevantMemories.Count} experiences, the agent tends to: {commonPatterns.Key}";
        }

        return $"Analysis of {relevantMemories.Count} experiences related to: {question}";
    }

    private readonly object _persistLock = new();

    private static readonly System.Text.Json.JsonSerializerOptions _jsonWriteIndented = new()
    {
        WriteIndented = true
    };

    private async Task PersistDreamResultAsync(DreamCycleResult result, CancellationToken ct)
    {
        try
        {
            lock (_persistLock)
            {
                List<DreamCycleResult> history;
                if (File.Exists(_dreamLogPath))
                {
                    var json = File.ReadAllText(_dreamLogPath);
                    history = System.Text.Json.JsonSerializer.Deserialize<List<DreamCycleResult>>(json) ?? new();
                }
                else
                {
                    history = new();
                }

                history.Add(result);

                if (history.Count > 100)
                    history = history.OrderByDescending(d => d.MemoriesConsolidated + d.KnowledgeDistilled).Take(50).ToList();

                File.WriteAllText(_dreamLogPath, System.Text.Json.JsonSerializer.Serialize(history, _jsonWriteIndented));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist dream result");
        }
    }

    private int ForgetWeakMemories()
    {
        var oldInteractions = _memory.GetExperiencesByType(SynapseType.Interaction, 500)
            .Where(e => e.Reward < 0.3f && (DateTime.UtcNow - e.CreatedAt).TotalHours > 24)
            .ToList();

        foreach (var exp in oldInteractions)
        {
            _memory.DeleteExperience(exp.Id);
        }

        var forgotten = oldInteractions.Count;
        _logger.LogInformation("Dream cycle: forgot {Count} weak memories (reward<0.3, age>24h)", forgotten);
        return forgotten;
    }

    private int MergeSimilarPatterns()
    {
        var teachings = _memory.GetExperiencesByType(SynapseType.Teaching, 200)
            .Where(e => !e.UsedForTraining)
            .ToList();

        var merged = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in teachings)
        {
            var normalized = NormalizeText(exp.Query);
            if (seen.Any(s => Similarity(normalized, s) > 0.8f))
            {
                merged++;
                continue;
            }
            seen.Add(normalized);
        }

        if (merged > 0)
            _logger.LogInformation("Dream cycle: merged {Count} similar patterns", merged);
        return merged;
    }

    private static string NormalizeText(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", "").Trim();
    }

    private static float Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0) return 0f;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0f;
    }

    private int DistillKnowledgeToGraph()
    {
        var teachings = _memory.GetExperiencesByType(SynapseType.Teaching, 50)
            .Where(e => !e.UsedForTraining)
            .Take(5)
            .ToList();

        var count = 0;
        foreach (var exp in teachings)
        {
            _graphBridge.IngestExperience(exp.Query, exp.Response, exp.Label);
            exp.UsedForTraining = true;
            count++;
        }

        return count;
    }

    public async Task ForceDreamAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Forced dream cycle triggered");
        await DreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 强制触发反思 (忽略阈值)
    /// </summary>
    public async Task ForceReflectionAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Forced reflection triggered");

        var reflectionsGenerated = await GenerateReflectionsAsync(ct, "forced");

        _logger.LogInformation("Forced reflection completed: generated={Count}", reflectionsGenerated);
    }

    /// <summary>
    /// 获取反思统计
    /// </summary>
    public ReflectionStats GetReflectionStats()
    {
        lock (_reflectionLock)
        {
            return new ReflectionStats
            {
                TotalReflections = _totalReflections,
                AccumulatedImportance = _accumulatedImportance,
                ImportanceThreshold = _reflectionConfig.ImportanceThreshold,
                LastReflectionAt = _lastReflection,
                RecentTriggers = _reflectionHistory.TakeLast(10).ToList()
            };
        }
    }
}

public record ReflectionStats
{
    public int TotalReflections { get; init; }
    public float AccumulatedImportance { get; init; }
    public float ImportanceThreshold { get; init; }
    public DateTime LastReflectionAt { get; init; }
    public List<ReflectionTrigger> RecentTriggers { get; init; } = new();
}
