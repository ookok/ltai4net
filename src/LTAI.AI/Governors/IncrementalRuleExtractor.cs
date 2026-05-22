using System.Text.Json;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum DeltaKind { Add, Update, Delete, Merge }

public record RuleDelta
{
    public string RuleId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DeltaKind Kind { get; init; }
    public string Domain { get; init; } = "";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public List<string> SourceEpisodeIds { get; init; } = new();
    public int HelpfulCount { get; init; }
    public int HarmfulCount { get; init; }
    public float Confidence { get; init; }
    public string MergeTargetId { get; init; } = "";  // 合并目标规则ID
}

public record DeltaApplicationResult
{
    public bool Success { get; init; }
    public int AppliedCount { get; init; }
    public int RejectedCount { get; init; }
    public List<string> Errors { get; init; } = new();
}

public sealed class IncrementalRuleExtractor
{
    private readonly DualMemoryStore _memoryStore;
    private readonly ILogger<IncrementalRuleExtractor> _logger;
    private readonly Dictionary<string, RuleDelta> _pendingDeltas = new();
    private readonly object _lock = new();

    public IncrementalRuleExtractor(
        DualMemoryStore memoryStore,
        ILogger<IncrementalRuleExtractor>? logger = null)
    {
        _memoryStore = memoryStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IncrementalRuleExtractor>.Instance;
    }

    /// <summary>
    /// 从新原始记忆中提取增量规则
    /// </summary>
    public async Task<List<RuleDelta>> ExtractDeltasAsync(
        List<RawEpisode> newEpisodes,
        CancellationToken ct = default)
    {
        var deltas = new List<RuleDelta>();

        // 按领域分组
        var byDomain = newEpisodes.GroupBy(e => e.Domain).ToList();

        foreach (var domainGroup in byDomain)
        {
            if (ct.IsCancellationRequested) break;

            var domain = domainGroup.Key;
            var episodes = domainGroup.ToList();

            // 提取新规则
            var newRules = await ExtractNewRulesAsync(episodes, domain, ct);
            deltas.AddRange(newRules);

            // 检查是否需要更新现有规则
            var updates = await FindUpdatesAsync(episodes, domain, ct);
            deltas.AddRange(updates);

            // 检查是否需要合并相似规则
            var merges = await FindMergesAsync(domain, ct);
            deltas.AddRange(merges);
        }

        _logger.LogInformation(
            "Delta extraction completed: new={New} updates={Updates} merges={Merges}",
            deltas.Count(d => d.Kind == DeltaKind.Add),
            deltas.Count(d => d.Kind == DeltaKind.Update),
            deltas.Count(d => d.Kind == DeltaKind.Merge));

        return deltas;
    }

    /// <summary>
    /// 递归抽象提取 - 从现有抽象记忆中提取高阶规则 (Meta-Rules)
    /// 对应 Generative Agents 论文的 Recursive Reflection 机制
    /// </summary>
    public async Task<List<RuleDelta>> ExtractMetaRulesAsync(
        string domain,
        int maxDepth = 2,
        CancellationToken ct = default)
    {
        var metaDeltas = new List<RuleDelta>();

        _logger.LogInformation(
            "Starting recursive abstraction extraction: domain={Domain} maxDepth={Depth}",
            domain, maxDepth);

        for (var depth = 1; depth <= maxDepth; depth++)
        {
            if (ct.IsCancellationRequested) break;

            // 获取当前层级的抽象记忆
            var currentLessons = _memoryStore.FindRelevantLessons(domain, limit: 50);
            if (currentLessons.Count < 5)
            {
                _logger.LogDebug(
                    "Insufficient lessons for meta-extraction at depth {Depth}: {Count}",
                    depth, currentLessons.Count);
                break;
            }

            // 提取高阶模式
            var metaRules = await ExtractMetaRulesFromLessonsAsync(currentLessons, domain, depth, ct);
            metaDeltas.AddRange(metaRules);

            _logger.LogInformation(
                "Meta-rules extracted at depth {Depth}: count={Count}",
                depth, metaRules.Count);
        }

        return metaDeltas;
    }

    /// <summary>
    /// 应用增量更新到记忆存储
    /// </summary>
    public async Task<DeltaApplicationResult> ApplyDeltasAsync(
        List<RuleDelta> deltas,
        CancellationToken ct = default)
    {
        var applied = 0;
        var rejected = 0;
        var errors = new List<string>();

        foreach (var delta in deltas)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                switch (delta.Kind)
                {
                    case DeltaKind.Add:
                        await ApplyAddDeltaAsync(delta, ct);
                        applied++;
                        break;

                    case DeltaKind.Update:
                        await ApplyUpdateDeltaAsync(delta, ct);
                        applied++;
                        break;

                    case DeltaKind.Delete:
                        await ApplyDeleteDeltaAsync(delta, ct);
                        applied++;
                        break;

                    case DeltaKind.Merge:
                        await ApplyMergeDeltaAsync(delta, ct);
                        applied++;
                        break;
                }
            }
            catch (Exception ex)
            {
                rejected++;
                errors.Add($"Failed to apply delta {delta.RuleId}: {ex.Message}");
                _logger.LogError(ex, "Failed to apply delta: {RuleId}", delta.RuleId);
            }
        }

        return new DeltaApplicationResult
        {
            Success = rejected == 0,
            AppliedCount = applied,
            RejectedCount = rejected,
            Errors = errors
        };
    }

    /// <summary>
    /// 获取待处理的增量
    /// </summary>
    public List<RuleDelta> GetPendingDeltas()
    {
        lock (_lock)
        {
            return _pendingDeltas.Values.ToList();
        }
    }

    /// <summary>
    /// 清除待处理的增量
    /// </summary>
    public void ClearPendingDeltas()
    {
        lock (_lock)
        {
            _pendingDeltas.Clear();
        }
    }

    // ==================== 内部方法 ====================

    /// <summary>
    /// 从抽象记忆中提取高阶规则 (Meta-Rules)
    /// </summary>
    private async Task<List<RuleDelta>> ExtractMetaRulesFromLessonsAsync(
        List<AbstractLesson> lessons,
        string domain,
        int depth,
        CancellationToken ct)
    {
        var metaDeltas = new List<RuleDelta>();

        // 按种类分组
        var byKind = lessons.GroupBy(l => l.Kind).ToList();

        foreach (var kindGroup in byKind)
        {
            if (ct.IsCancellationRequested) break;

            var kind = kindGroup.Key;
            var kindLessons = kindGroup.ToList();

            if (kindLessons.Count < 3) continue;

            // 提取共同高阶模式
            var metaPattern = ExtractCommonMetaPattern(kindLessons, kind, depth);
            if (metaPattern != null)
            {
                var metaDelta = new RuleDelta
                {
                    Kind = DeltaKind.Add,
                    Domain = domain,
                    Title = $"[Meta-{depth}] {metaPattern.Title}",
                    Content = metaPattern.Content,
                    SourceEpisodeIds = kindLessons.SelectMany(l => l.SourceEpisodeIds).Distinct().Take(20).ToList(),
                    HelpfulCount = kindLessons.Sum(l => l.HelpfulCount),
                    HarmfulCount = kindLessons.Sum(l => l.HarmfulCount),
                    Confidence = ComputeMetaConfidence(kindLessons, depth)
                };

                metaDeltas.Add(metaDelta);
            }
        }

        return metaDeltas;
    }

    /// <summary>
    /// 提取共同高阶模式
    /// </summary>
    private static MetaPatternInfo? ExtractCommonMetaPattern(List<AbstractLesson> lessons, LessonKind kind, int depth)
    {
        // 提取所有课程内容的共同词汇
        var allWords = lessons
            .SelectMany(l => l.Content.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 4)  // 过滤短词
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        if (allWords.Count < 3) return null;

        var topWords = allWords.Select(g => g.Key).Take(5).ToList();
        var highQualityLessons = lessons.Where(l => l.QualityScore > 0.7f).ToList();

        if (highQualityLessons.Count < 2) return null;

        // 构建高阶模式内容
        var content = $"At abstraction level {depth}, {kind} patterns in this domain consistently involve: {string.Join(", ", topWords)}. " +
                      $"Based on {highQualityLessons.Count} high-quality lessons (avg quality: {highQualityLessons.Average(l => l.QualityScore):F2}).";

        return new MetaPatternInfo
        {
            Title = $"{kind} meta-pattern (depth {depth})",
            Content = content
        };
    }

    /// <summary>
    /// 计算高阶规则置信度 (随深度递减)
    /// </summary>
    private static float ComputeMetaConfidence(List<AbstractLesson> lessons, int depth)
    {
        var baseConfidence = (float)lessons.Average(l => l.QualityScore);
        var depthPenalty = 0.1f * (depth - 1);  // 每层深度降低 0.1
        var sampleBonus = Math.Min(0.1f, lessons.Count / 100f);

        return Math.Max(0.3f, Math.Min(1.0f, baseConfidence - depthPenalty + sampleBonus));
    }

    private record MetaPatternInfo
    {
        public string Title { get; init; } = "";
        public string Content { get; init; } = "";
    }

    private async Task<List<RuleDelta>> ExtractNewRulesAsync(
        List<RawEpisode> episodes,
        string domain,
        CancellationToken ct)
    {
        var deltas = new List<RuleDelta>();

        // 查找成功模式的共同特征
        var successfulEpisodes = episodes.Where(e => e.WasSuccessful && e.Reward > 0.6f).ToList();
        if (successfulEpisodes.Count < 3)  // 至少需要3个成功案例
            return deltas;

        // 提取共同策略
        var commonPatterns = ExtractCommonPatterns(successfulEpisodes);

        foreach (var pattern in commonPatterns)
        {
            if (ct.IsCancellationRequested) break;

            var delta = new RuleDelta
            {
                Kind = DeltaKind.Add,
                Domain = domain,
                Title = pattern.Title,
                Content = pattern.Content,
                SourceEpisodeIds = pattern.SourceEpisodeIds,
                HelpfulCount = pattern.Confidence > 0.8f ? 1 : 0,
                HarmfulCount = 0,
                Confidence = pattern.Confidence
            };

            deltas.Add(delta);
        }

        return deltas;
    }

    private async Task<List<RuleDelta>> FindUpdatesAsync(
        List<RawEpisode> newEpisodes,
        string domain,
        CancellationToken ct)
    {
        var deltas = new List<RuleDelta>();

        // 获取现有规则
        var existingLessons = _memoryStore.FindRelevantLessons(domain);

        foreach (var lesson in existingLessons)
        {
            if (ct.IsCancellationRequested) break;
            if (lesson.IsFrozen) continue;  // 冻结的规则不更新

            // 检查是否有新的证据支持或反对该规则
            var supportingEpisodes = newEpisodes
                .Where(e => SupportsRule(e, lesson))
                .ToList();

            var contradictingEpisodes = newEpisodes
                .Where(e => ContradictsRule(e, lesson))
                .ToList();

            if (supportingEpisodes.Count > contradictingEpisodes.Count * 2)
            {
                // 规则得到加强
                deltas.Add(new RuleDelta
                {
                    Kind = DeltaKind.Update,
                    RuleId = lesson.Id.ToString(),
                    Domain = domain,
                    Title = lesson.Title,
                    Content = lesson.Content,
                    HelpfulCount = lesson.HelpfulCount + supportingEpisodes.Count,
                    HarmfulCount = lesson.HarmfulCount,
                    Confidence = ComputeUpdatedConfidence(
                        lesson.HelpfulCount + supportingEpisodes.Count,
                        lesson.HarmfulCount)
                });
            }
            else if (contradictingEpisodes.Count > supportingEpisodes.Count * 2)
            {
                // 规则被削弱
                deltas.Add(new RuleDelta
                {
                    Kind = DeltaKind.Update,
                    RuleId = lesson.Id.ToString(),
                    Domain = domain,
                    Title = lesson.Title,
                    Content = lesson.Content,
                    HelpfulCount = lesson.HelpfulCount,
                    HarmfulCount = lesson.HarmfulCount + contradictingEpisodes.Count,
                    Confidence = ComputeUpdatedConfidence(
                        lesson.HelpfulCount,
                        lesson.HarmfulCount + contradictingEpisodes.Count)
                });
            }
        }

        return deltas;
    }

    private async Task<List<RuleDelta>> FindMergesAsync(
        string domain,
        CancellationToken ct)
    {
        var deltas = new List<RuleDelta>();

        var lessons = _memoryStore.FindRelevantLessons(domain, limit: 50);

        // 查找相似规则
        for (var i = 0; i < lessons.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            for (var j = i + 1; j < lessons.Count; j++)
            {
                var similarity = ComputeRuleSimilarity(lessons[i], lessons[j]);
                if (similarity > 0.8f)  // 80%相似度视为重复
                {
                    // 合并到质量更高的规则
                    var target = lessons[i].QualityScore >= lessons[j].QualityScore
                        ? lessons[i]
                        : lessons[j];
                    var source = lessons[i].QualityScore >= lessons[j].QualityScore
                        ? lessons[j]
                        : lessons[i];

                    deltas.Add(new RuleDelta
                    {
                        Kind = DeltaKind.Merge,
                        RuleId = source.Id.ToString(),
                        MergeTargetId = target.Id.ToString(),
                        Domain = domain,
                        Title = target.Title,
                        Content = MergeRuleContent(target.Content, source.Content),
                        HelpfulCount = target.HelpfulCount + source.HelpfulCount,
                        HarmfulCount = target.HarmfulCount + source.HarmfulCount,
                        Confidence = ComputeUpdatedConfidence(
                            target.HelpfulCount + source.HelpfulCount,
                            target.HarmfulCount + source.HarmfulCount)
                    });
                }
            }
        }

        return deltas;
    }

    private async Task ApplyAddDeltaAsync(RuleDelta delta, CancellationToken ct)
    {
        var lesson = new AbstractLesson
        {
            Title = delta.Title,
            Kind = LessonKind.Rule,
            Content = delta.Content,
            Domain = delta.Domain,
            SourceEpisodeIds = delta.SourceEpisodeIds,
            HelpfulCount = delta.HelpfulCount,
            HarmfulCount = delta.HarmfulCount,
            QualityScore = delta.Confidence,
            Version = 1
        };

        _memoryStore.StoreLesson(lesson);

        _logger.LogDebug(
            "Delta applied (Add): domain={Domain} title={Title}",
            delta.Domain, delta.Title);
    }

    private async Task ApplyUpdateDeltaAsync(RuleDelta delta, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(delta.RuleId)) return;

        var lessonId = new ObjectId(delta.RuleId);
        _memoryStore.ReportLessonFeedback(lessonId, delta.HelpfulCount > delta.HarmfulCount);

        _logger.LogDebug(
            "Delta applied (Update): ruleId={RuleId} helpful={Helpful} harmful={Harmful}",
            delta.RuleId, delta.HelpfulCount, delta.HarmfulCount);
    }

    private async Task ApplyDeleteDeltaAsync(RuleDelta delta, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(delta.RuleId)) return;

        var lessonId = new ObjectId(delta.RuleId);
        _memoryStore.FreezeLesson(lessonId);  // 冻结而非删除

        _logger.LogDebug("Delta applied (Delete/Freeze): ruleId={RuleId}", delta.RuleId);
    }

    private async Task ApplyMergeDeltaAsync(RuleDelta delta, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(delta.MergeTargetId)) return;

        var targetId = new ObjectId(delta.MergeTargetId);
        _memoryStore.ReportLessonFeedback(targetId, true);

        if (!string.IsNullOrEmpty(delta.RuleId))
        {
            var sourceId = new ObjectId(delta.RuleId);
            _memoryStore.FreezeLesson(sourceId);  // 冻结源规则
        }

        _logger.LogDebug(
            "Delta applied (Merge): source={Source} target={Target}",
            delta.RuleId, delta.MergeTargetId);
    }

    // ==================== 辅助方法 ====================

    private record PatternInfo
    {
        public string Title { get; init; } = "";
        public string Content { get; init; } = "";
        public List<string> SourceEpisodeIds { get; init; } = new();
        public float Confidence { get; init; }
    }

    private static List<PatternInfo> ExtractCommonPatterns(List<RawEpisode> episodes)
    {
        var patterns = new List<PatternInfo>();

        // 简单模式：基于查询关键词提取
        var allKeywords = episodes
            .SelectMany(e => e.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        if (allKeywords.Count > 0)
        {
            var topKeywords = allKeywords.Select(g => g.Key).Take(3).ToList();
            var matchingEpisodes = episodes
                .Where(e => topKeywords.Any(kw => e.Query.ToLowerInvariant().Contains(kw)))
                .ToList();

            if (matchingEpisodes.Count >= 3)
            {
                patterns.Add(new PatternInfo
                {
                    Title = $"Common pattern: {string.Join(", ", topKeywords)}",
                    Content = $"Queries containing {string.Join(", ", topKeywords)} tend to succeed with approach: {matchingEpisodes[0].FinalAnswer[..Math.Min(100, matchingEpisodes[0].FinalAnswer.Length)]}",
                    SourceEpisodeIds = matchingEpisodes.Select(e => e.Id.ToString()).ToList(),
                    Confidence = (float)matchingEpisodes.Count / episodes.Count
                });
            }
        }

        return patterns;
    }

    private static bool SupportsRule(RawEpisode episode, AbstractLesson lesson)
    {
        return episode.WasSuccessful &&
               episode.Query.Contains(lesson.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContradictsRule(RawEpisode episode, AbstractLesson lesson)
    {
        return !episode.WasSuccessful &&
               episode.Query.Contains(lesson.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static float ComputeUpdatedConfidence(int helpful, int harmful)
    {
        var total = helpful + harmful;
        if (total == 0) return 0.5f;

        var ratio = (float)helpful / total;
        var confidenceBonus = Math.Min(0.1f, total / 100f);

        return Math.Min(1.0f, ratio + confidenceBonus);
    }

    private static float ComputeRuleSimilarity(AbstractLesson a, AbstractLesson b)
    {
        var wordsA = a.Content.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Content.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0f;
    }

    private static string MergeRuleContent(string primary, string secondary)
    {
        // 简单合并：保留主要内容，附加次要内容作为补充
        return $"{primary}\n\nAdditional context: {secondary}";
    }
}
