using System.Collections.Concurrent;
using LiteDB;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 原始记忆层 (Episodic Store) ====================

public record RawEpisode
{
    [BsonId]
    public ObjectId Id { get; init; } = default!;
    public string Query { get; init; } = "";
    public string FullTrajectory { get; init; } = "";  // 完整推理过程
    public string FinalAnswer { get; init; } = "";
    public string Domain { get; init; } = "";
    public bool WasSuccessful { get; init; }
    public float Confidence { get; init; }
    public float Reward { get; init; }
    public float ImportanceScore { get; init; } = 5.0f;  // 1-10 重要性评分
    public float[]? Embedding { get; init; }  // 向量嵌入
    public string Metadata { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public bool IsImmutable { get; init; } = true;  // 原始记忆不可变
}

// ==================== 抽象记忆层 (Abstract Store) ====================

public enum LessonKind { Strategy, Rule, Pattern, Warning, CodeSnippet }

public record AbstractLesson
{
    [BsonId]
    public ObjectId Id { get; init; } = default!;
    public string Title { get; init; } = "";
    public LessonKind Kind { get; init; }
    public string Content { get; init; } = "";
    public string Domain { get; init; } = "";
    public List<string> SourceEpisodeIds { get; init; } = new();  // 来源原始记忆ID
    public int HelpfulCount { get; init; }
    public int HarmfulCount { get; init; }
    public float QualityScore { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastUpdatedAt { get; init; }
    public int Version { get; init; } = 1;
    public bool IsFrozen { get; init; }  // 冻结后不再更新
}

// ==================== 记忆整合配置 ====================

public record ConsolidationConfig
{
    public int MinEpisodesToConsolidate { get; init; } = 50;  // 最少原始记忆数
    public float QualityThreshold { get; init; } = 0.6f;  // 质量阈值
    public float ImprovementThreshold { get; init; } = 0.1f;  // 需要10%改进才整合
    public int MaxConsolidationPerCycle { get; init; } = 10;  // 每周期最大整合数
    public TimeSpan ConsolidationCooldown { get; init; } = TimeSpan.FromHours(1);  // 整合冷却时间
    public bool EnableGatedConsolidation { get; init; } = true;  // 启用门控整合
}

// ==================== 双记忆系统 ====================

public sealed class DualMemoryStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RawEpisode> _episodes;
    private readonly ILiteCollection<AbstractLesson> _lessons;
    private readonly ILogger<DualMemoryStore> _logger;
    private readonly ConsolidationConfig _config;
    private readonly RetrievalWeights _retrievalWeights;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly object _lock = new();
    private DateTime _lastConsolidation = DateTime.MinValue;
    private int _totalConsolidations;
    private int _rejectedConsolidations;

    public DualMemoryStore(
        string dbPath,
        ConsolidationConfig? config = null,
        RetrievalWeights? retrievalWeights = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        ILogger<DualMemoryStore>? logger = null)
    {
        _config = config ?? new ConsolidationConfig();
        _retrievalWeights = retrievalWeights ?? new RetrievalWeights();
        _embeddingGenerator = embeddingGenerator;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DualMemoryStore>.Instance;

        var connectionString = $"Filename={dbPath};Connection=Shared";
        _db = new LiteDatabase(connectionString);

        _episodes = _db.GetCollection<RawEpisode>("episodes");
        _episodes.EnsureIndex(x => x.Domain);
        _episodes.EnsureIndex(x => x.WasSuccessful);
        _episodes.EnsureIndex(x => x.Timestamp);

        _lessons = _db.GetCollection<AbstractLesson>("lessons");
        _lessons.EnsureIndex(x => x.Domain);
        _lessons.EnsureIndex(x => x.Kind);
        _lessons.EnsureIndex(x => x.QualityScore);
        _lessons.EnsureIndex(x => x.IsFrozen);

        _logger.LogInformation(
            "DualMemoryStore initialized: episodes={Episodes} lessons={Lessons} gated={Gated}",
            _episodes.Count(), _lessons.Count(), _config.EnableGatedConsolidation);
    }

    // ==================== 原始记忆操作 (Episodic Store) ====================

    /// <summary>
    /// 存储原始记忆 - 追加不可变
    /// </summary>
    public async Task StoreEpisodeAsync(RawEpisode episode)
    {
        // 自动生成向量 (如果配置了 EmbeddingGenerator)
        if (_embeddingGenerator != null && episode.Embedding == null)
        {
            try
            {
                var embeddings = await _embeddingGenerator.GenerateAsync(new[] { episode.Query }).ConfigureAwait(false);
                if (embeddings.Count > 0)
                {
                    // Embedding<T> exposes Vector as ReadOnlyMemory<T>
                    episode = episode with { Embedding = embeddings[0].Vector.ToArray() };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate embedding for episode");
            }
        }

        lock (_lock)
        {
            _episodes.Insert(episode);
        }

        _logger.LogDebug(
            "Episode stored: domain={Domain} success={Success} id={Id}",
            episode.Domain, episode.WasSuccessful, episode.Id);
    }

    /// <summary>
    /// 存储原始记忆 (同步包装器)
    /// </summary>
    public void StoreEpisode(RawEpisode episode)
    {
        Task.Run(() => StoreEpisodeAsync(episode)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 批量存储原始记忆
    /// </summary>
    public void StoreEpisodesBatch(IEnumerable<RawEpisode> episodes)
    {
        lock (_lock)
        {
            _episodes.InsertBulk(episodes);
        }

        _logger.LogInformation("Batch episodes stored: count={Count}", episodes.Count());
    }

    /// <summary>
    /// 检索相似原始记忆 - 使用向量相似度 (优先) 或 统一评分公式
    /// </summary>
    public async Task<List<RawEpisode>> FindSimilarEpisodesAsync(string query, string? domain = null, int limit = 10)
    {
        float[]? queryEmbedding = null;

        // 1. 尝试生成查询向量
        if (_embeddingGenerator != null)
        {
            try
            {
                var embeddings = await _embeddingGenerator.GenerateAsync(new[] { query }).ConfigureAwait(false);
                if (embeddings.Count > 0)
                {
                    queryEmbedding = embeddings[0].Vector.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding");
            }
        }

        // 2. 获取候选集
        var queryable = _episodes.Query()
            .Where(x => x.WasSuccessful);
        
        if (!string.IsNullOrEmpty(domain))
        {
            queryable = _episodes.Query()
                .Where(x => x.WasSuccessful && x.Domain == domain);
        }

        // 获取较多候选进行排序
        var candidates = queryable.OrderByDescending(x => x.Timestamp).Limit(limit * 5).ToList();
        var now = DateTime.UtcNow;

        // 3. 排序
        var scored = candidates
            .Select(e => new
            {
                Episode = e,
                Score = queryEmbedding != null && e.Embedding != null
                    ? ComputeVectorScore(e, queryEmbedding, now) // 向量模式
                    : ComputeTextScore(e, query, now)            // 文本模式
            })
            .OrderByDescending(x => x.Score.TotalScore)
            .Take(limit)
            .Select(x => x.Episode)
            .ToList();

        return scored;
    }

    /// <summary>
    /// 同步包装器
    /// </summary>
    public List<RawEpisode> FindSimilarEpisodes(string query, string? domain = null, int limit = 10)
    {
        return Task.Run(() => FindSimilarEpisodesAsync(query, domain, limit)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 计算向量相似度得分 (Cosine Similarity + Recency + Importance)
    /// </summary>
    private MemoryRetrievalScore ComputeVectorScore(RawEpisode episode, float[] queryEmbedding, DateTime now)
    {
        var w = _retrievalWeights;
        var cosineSim = ComputeCosineSimilarity(queryEmbedding, episode.Embedding!);
        
        // Recency
        var minutesSinceCreation = (now - episode.Timestamp).TotalMinutes;
        var recency = Math.Pow(w.RecencyDecayRate, minutesSinceCreation / 1440.0);

        // Importance
        var importance = Math.Clamp(episode.ImportanceScore / 10.0, 0.0, 1.0);

        var totalScore = w.RecencyWeight * recency +
                         w.ImportanceWeight * importance +
                         w.RelevanceWeight * cosineSim;

        return new MemoryRetrievalScore
        {
            Recency = recency,
            Importance = importance,
            Relevance = cosineSim,
            TotalScore = totalScore,
            MemoryId = episode.Id.ToString()
        };
    }

    /// <summary>
    /// 计算文本相似度得分 (Jaccard + Recency + Importance)
    /// </summary>
    private MemoryRetrievalScore ComputeTextScore(RawEpisode episode, string query, DateTime now)
    {
        var w = _retrievalWeights;
        var textSim = ComputeSemanticRelevance(query, episode);
        
        var minutesSinceCreation = (now - episode.Timestamp).TotalMinutes;
        var recency = Math.Pow(w.RecencyDecayRate, minutesSinceCreation / 1440.0);
        var importance = Math.Clamp(episode.ImportanceScore / 10.0, 0.0, 1.0);

        var totalScore = w.RecencyWeight * recency +
                         w.ImportanceWeight * importance +
                         w.RelevanceWeight * textSim;

        return new MemoryRetrievalScore
        {
            Recency = recency,
            Importance = importance,
            Relevance = textSim,
            TotalScore = totalScore,
            MemoryId = episode.Id.ToString()
        };
    }

    private static double ComputeCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;
        
        double dotProduct = 0.0;
        double normA = 0.0;
        double normB = 0.0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0.0 || normB == 0.0) return 0.0;
        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>
    /// 计算语义相关性 (可扩展为向量余弦相似度)
    /// </summary>
    private double ComputeSemanticRelevance(string query, RawEpisode episode)
    {
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var episodeWords = episode.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = queryWords.Intersect(episodeWords).Count();
        var union = queryWords.Union(episodeWords).Count();

        // 基础 Jaccard 相似度
        var jaccard = union > 0 ? (double)intersection / union : 0.0;

        // 领域匹配奖励
        var domainBonus = query.Contains(episode.Domain, StringComparison.OrdinalIgnoreCase) ? 0.1 : 0.0;

        // 成功经历奖励
        var successBonus = episode.WasSuccessful ? 0.05 : 0.0;

        return Math.Min(1.0, jaccard + domainBonus + successBonus);
    }

    /// <summary>
    /// 获取指定领域的原始记忆
    /// </summary>
    public List<RawEpisode> GetEpisodesByDomain(string domain, int limit = 100)
    {
        return _episodes.Query()
            .Where(x => x.Domain == domain)
            .OrderByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToList();
    }

    /// <summary>
    /// 获取未整合的原始记忆
    /// </summary>
    public List<RawEpisode> GetUnconsolidatedEpisodes(int limit = 500)
    {
        var consolidatedEpisodeIds = _lessons.Query()
            .ToList()
            .SelectMany(x => x.SourceEpisodeIds)
            .Distinct()
            .Select(id => new ObjectId(id))
            .ToHashSet();

        return _episodes.Query()
            .Where(x => x.WasSuccessful && x.Reward > 0.5f)
            .OrderByDescending(x => x.Reward)
            .Limit(limit * 2)
            .ToList()
            .Where(e => !consolidatedEpisodeIds.Contains(e.Id))
            .Take(limit)
            .ToList();
    }

    // ==================== 抽象记忆操作 (Abstract Store) ====================

    /// <summary>
    /// 存储抽象教训
    /// </summary>
    public void StoreLesson(AbstractLesson lesson)
    {
        lock (_lock)
        {
            _lessons.Insert(lesson);
        }

        _logger.LogInformation(
            "Lesson stored: title={Title} domain={Domain} kind={Kind}",
            lesson.Title, lesson.Domain, lesson.Kind);
    }

    /// <summary>
    /// 检索相关抽象教训
    /// </summary>
    public List<AbstractLesson> FindRelevantLessons(string domain, LessonKind? kind = null, int limit = 5)
    {
        var queryable = _lessons.Query()
            .Where(x => x.Domain == domain && x.QualityScore > _config.QualityThreshold && !x.IsFrozen)
            .OrderByDescending(x => x.QualityScore)
            .Limit(limit * 3)
            .ToList();

        if (kind.HasValue)
        {
            queryable = _lessons.Query()
                .Where(x => x.Domain == domain && x.Kind == kind.Value && x.QualityScore > _config.QualityThreshold && !x.IsFrozen)
                .OrderByDescending(x => x.QualityScore)
                .Limit(limit * 3)
                .ToList();
        }

        return queryable
            .OrderByDescending(x => x.QualityScore)
            .ThenByDescending(x => x.HelpfulCount)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// 报告教训的使用反馈
    /// </summary>
    public void ReportLessonFeedback(ObjectId lessonId, bool wasHelpful)
    {
        var lesson = _lessons.FindById(lessonId);
        if (lesson == null) return;

        var updated = lesson with
        {
            HelpfulCount = wasHelpful ? lesson.HelpfulCount + 1 : lesson.HelpfulCount,
            HarmfulCount = wasHelpful ? lesson.HarmfulCount : lesson.HarmfulCount + 1,
            QualityScore = ComputeQualityScore(wasHelpful ? lesson.HelpfulCount + 1 : lesson.HelpfulCount,
                                              wasHelpful ? lesson.HarmfulCount : lesson.HarmfulCount + 1)
        };

        _lessons.Update(updated);

        _logger.LogDebug(
            "Lesson feedback recorded: id={Id} helpful={Helpful} quality={Quality:F2}",
            lessonId, wasHelpful, updated.QualityScore);
    }

    /// <summary>
    /// 冻结教训（不再更新）
    /// </summary>
    public void FreezeLesson(ObjectId lessonId)
    {
        var lesson = _lessons.FindById(lessonId);
        if (lesson == null) return;

        var updated = lesson with { IsFrozen = true };
        _lessons.Update(updated);

        _logger.LogInformation("Lesson frozen: id={Id} title={Title}", lessonId, lesson.Title);
    }

    // ==================== 门控整合逻辑 ====================

    /// <summary>
    /// 检查是否应该进行整合
    /// </summary>
    public bool ShouldConsolidate()
    {
        if (!_config.EnableGatedConsolidation)
            return false;

        var timeSinceLastConsolidation = DateTime.UtcNow - _lastConsolidation;
        if (timeSinceLastConsolidation < _config.ConsolidationCooldown)
            return false;

        var unconsolidatedCount = GetUnconsolidatedEpisodes(_config.MinEpisodesToConsolidate * 2).Count;
        return unconsolidatedCount >= _config.MinEpisodesToConsolidate;
    }

    /// <summary>
    /// 执行门控整合
    /// </summary>
    public async Task<ConsolidationResult> ConsolidateIfNeededAsync(
        Func<List<RawEpisode>, Task<List<AbstractLesson>>> lessonExtractor,
        CancellationToken ct = default)
    {
        if (!ShouldConsolidate())
        {
            return new ConsolidationResult
            {
                Success = false,
                Reason = "Consolidation not needed (gated or cooldown)"
            };
        }

        var episodes = GetUnconsolidatedEpisodes(_config.MinEpisodesToConsolidate * 2);
        if (episodes.Count < _config.MinEpisodesToConsolidate)
        {
            return new ConsolidationResult
            {
                Success = false,
                Reason = $"Insufficient episodes: {episodes.Count} < {_config.MinEpisodesToConsolidate}"
            };
        }

        try
        {
            // 提取教训
            var newLessons = await lessonExtractor(episodes.Take(_config.MaxConsolidationPerCycle).ToList()).ConfigureAwait(false);

            if (newLessons.Count == 0)
            {
                _rejectedConsolidations++;
                return new ConsolidationResult
                {
                    Success = false,
                    Reason = "No lessons extracted"
                };
            }

            // 质量检查
            var qualifiedLessons = newLessons
                .Where(l => l.QualityScore >= _config.QualityThreshold)
                .ToList();

            if (qualifiedLessons.Count == 0)
            {
                _rejectedConsolidations++;
                return new ConsolidationResult
                {
                    Success = false,
                    Reason = "No lessons passed quality threshold"
                };
            }

            // 存储教训
            lock (_lock)
            {
                foreach (var lesson in qualifiedLessons)
                {
                    _lessons.Insert(lesson);
                }
            }

            _lastConsolidation = DateTime.UtcNow;
            _totalConsolidations++;

            _logger.LogInformation(
                "Consolidation completed: extracted={Extracted} qualified={Qualified} total={Total}",
                newLessons.Count, qualifiedLessons.Count, _totalConsolidations);

            return new ConsolidationResult
            {
                Success = true,
                ExtractedLessons = newLessons.Count,
                QualifiedLessons = qualifiedLessons.Count,
                EpisodesProcessed = episodes.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consolidation failed");
            return new ConsolidationResult
            {
                Success = false,
                Reason = $"Exception: {ex.Message}"
            };
        }
    }

    // ==================== 统计和监控 ====================

    public MemoryStats GetStats()
    {
        var lessons = _lessons.Query().ToList();
        return new MemoryStats
        {
            TotalEpisodes = _episodes.Count(),
            TotalLessons = lessons.Count,
            UnconsolidatedEpisodes = GetUnconsolidatedEpisodes(int.MaxValue).Count,
            FrozenLessons = lessons.Count(x => x.IsFrozen),
            TotalConsolidations = _totalConsolidations,
            RejectedConsolidations = _rejectedConsolidations,
            LastConsolidation = _lastConsolidation,
            AverageLessonQuality = lessons.Count > 0 ? (float)lessons.Average(x => (double)x.QualityScore) : 0f
        };
    }

    // ==================== 内部方法 ====================

    /// <summary>
    /// 自动计算重要性评分 (基于奖励、置信度、领域稀有性)
    /// </summary>
    public static float ComputeImportanceScore(RawEpisode episode, int totalEpisodesInDomain = 100)
    {
        // 基础重要性来自奖励和置信度
        var baseImportance = (episode.Reward + episode.Confidence) / 2.0f;

        // 高奖励事件更重要
        var rewardBonus = episode.Reward > 0.8f ? 0.2f : 0.0f;

        // 低置信度成功可能代表学习机会
        var learningBonus = episode.WasSuccessful && episode.Confidence < 0.6f ? 0.1f : 0.0f;

        // 稀有领域事件更重要
        var rarityBonus = totalEpisodesInDomain < 20 ? 0.15f : 0.0f;

        var score = baseImportance * 5.0f + rewardBonus * 10.0f + learningBonus * 10.0f + rarityBonus * 10.0f;

        return Math.Clamp(score, 1.0f, 10.0f);
    }

    private static float ComputeQualityScore(int helpful, int harmful)
    {
        var total = helpful + harmful;
        if (total == 0) return 0.5f;

        var ratio = (float)helpful / total;
        var confidenceBonus = Math.Min(0.1f, total / 100f);

        return Math.Min(1.0f, ratio + confidenceBonus);
    }

    public void Dispose()
    {
        try
        {
            _db.Dispose();
            _logger.LogInformation("DualMemoryStore disposed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing DualMemoryStore");
        }
    }
}

// ==================== 结果和统计记录 ====================

public sealed record ConsolidationResult
{
    public bool Success { get; init; }
    public string Reason { get; init; } = "";
    public int ExtractedLessons { get; init; }
    public int QualifiedLessons { get; init; }
    public int EpisodesProcessed { get; init; }
}

public sealed record MemoryStats
{
    public int TotalEpisodes { get; init; }
    public int TotalLessons { get; init; }
    public int UnconsolidatedEpisodes { get; init; }
    public int FrozenLessons { get; init; }
    public int TotalConsolidations { get; init; }
    public int RejectedConsolidations { get; init; }
    public DateTime LastConsolidation { get; init; }
    public float AverageLessonQuality { get; init; }
}

// ==================== 统一检索评分 ====================

public record MemoryRetrievalScore
{
    public double Recency { get; init; }
    public double Importance { get; init; }
    public double Relevance { get; init; }
    public double TotalScore { get; init; }
    public string MemoryId { get; init; } = "";
}

public record RetrievalWeights
{
    public double RecencyWeight { get; init; } = 1.0;
    public double ImportanceWeight { get; init; } = 2.0;
    public double RelevanceWeight { get; init; } = 3.0;
    public double RecencyDecayRate { get; init; } = 0.99;  // 每日衰减率
}
