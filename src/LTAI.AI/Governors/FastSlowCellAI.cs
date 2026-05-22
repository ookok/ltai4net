using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== Fast-Slow Cell AI 核心组件 ====================

public record FastContext
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Domain { get; init; } = "";
    public string Prompt { get; init; } = "";
    public List<string> Examples { get; init; } = new();
    public List<string> Lessons { get; init; } = new();
    public float Accuracy { get; init; }
    public int UsageCount { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
    public bool IsFrozen { get; init; }
}

public record SlowModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Domain { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public float Accuracy { get; init; }
    public int TrainingSamples { get; init; }
    public DateTime LastTrained { get; init; }
    public bool IsActive { get; init; }
}

public record InteractionResult
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string Domain { get; init; } = "";
    public bool WasSuccessful { get; init; }
    public float Reward { get; init; }
    public string? Error { get; init; }
    public List<string> ReasoningSteps { get; init; } = new();
    public List<string> ToolCalls { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public record CellResult
{
    public bool Activated { get; init; }
    public string Response { get; init; } = "";
    public float Confidence { get; init; }
    public float LatencyMs { get; init; }
    public string Source { get; init; } = "";  // "fast", "slow", "combined"
    public string Domain { get; init; } = "";
}

public record FastSlowConfig
{
    public int FastUpdateInterval { get; init; } = 10;  // 每10次交互更新快速上下文
    public int SlowUpdateInterval { get; init; } = 50;  // 每50次交互更新慢速模型
    public float FastWeight { get; init; } = 0.6f;      // 快速结果权重
    public float SlowWeight { get; init; } = 0.4f;      // 慢速结果权重
    public int MaxFastContexts { get; init; } = 20;     // 最大快速上下文数
    public int MinSamplesForSlowTraining { get; init; } = 30;  // 慢速训练最小样本数
    public bool EnableCoEvolution { get; init; } = true;  // 启用协同进化
}

public sealed class FastSlowCellAI : IDisposable
{
    private readonly ConcurrentDictionary<string, FastContext> _fastContexts = new();
    private readonly ConcurrentDictionary<string, SlowModel> _slowModels = new();
    private readonly ConcurrentQueue<InteractionResult> _interactionBuffer = new();
    private readonly CellAIRegistry _cellRegistry;
    private readonly DualMemoryStore _memoryStore;
    private readonly GEPAPromptOptimizer _promptOptimizer;
    private readonly ILogger<FastSlowCellAI> _logger;
    private readonly FastSlowConfig _config;
    private readonly object _lock = new();
    
    private int _totalInteractions;
    private int _fastUpdates;
    private int _slowUpdates;
    private DateTime _lastFastUpdate = DateTime.UtcNow;
    private DateTime _lastSlowUpdate = DateTime.UtcNow;

    public FastSlowCellAI(
        CellAIRegistry cellRegistry,
        DualMemoryStore memoryStore,
        GEPAPromptOptimizer promptOptimizer,
        FastSlowConfig? config = null,
        ILogger<FastSlowCellAI>? logger = null)
    {
        _cellRegistry = cellRegistry;
        _memoryStore = memoryStore;
        _promptOptimizer = promptOptimizer;
        _config = config ?? new FastSlowConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FastSlowCellAI>.Instance;

        _logger.LogInformation(
            "FastSlowCellAI initialized: fastInterval={Fast} slowInterval={Slow} coEvolution={CoEvolve}",
            _config.FastUpdateInterval, _config.SlowUpdateInterval, _config.EnableCoEvolution);
    }

    // ==================== 核心处理流程 ====================

    public async Task<CellResult> ProcessAsync(string query, CancellationToken ct = default)
    {
        var domain = _cellRegistry.DetectDomain(query);

        // Fast loop: 使用动态上下文快速适应
        var fastResult = await ProcessWithFastContextAsync(query, domain, ct);

        // Slow loop: 使用模型进行深度推理
        var slowResult = await ProcessWithSlowModelAsync(query, domain, ct);

        // 协同决策
        var combinedResult = CombineFastSlowResults(fastResult, slowResult, domain);

        // 记录交互结果
        RecordInteraction(query, combinedResult, domain);

        // 检查是否需要更新
        await CheckAndUpdateAsync(ct);

        return combinedResult;
    }

    // ==================== Fast Loop ====================

    private async Task<CellResult> ProcessWithFastContextAsync(
        string query, string domain, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 获取相关快速上下文
        var relevantContexts = GetRelevantFastContexts(domain);
        if (relevantContexts.Count == 0)
        {
            return new CellResult
            {
                Activated = false,
                Source = "fast",
                Domain = domain
            };
        }

        // 使用快速上下文增强查询
        var enhancedQuery = BuildEnhancedQuery(query, relevantContexts);

        // 检索相似案例
        var episodes = _memoryStore.FindSimilarEpisodes(enhancedQuery, domain, limit: 3);
        if (episodes.Count > 0)
        {
            var bestEpisode = episodes[0];
            stopwatch.Stop();

            return new CellResult
            {
                Activated = true,
                Response = bestEpisode.FinalAnswer,
                Confidence = bestEpisode.Reward,
                LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds,
                Source = "fast",
                Domain = domain
            };
        }

        stopwatch.Stop();
        return new CellResult
        {
            Activated = false,
            Source = "fast",
            Domain = domain
        };
    }

    // ==================== Slow Loop ====================

    private async Task<CellResult> ProcessWithSlowModelAsync(
        string query, string domain, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 尝试使用 Cell AI Registry（自训练模型）
        var cellResult = _cellRegistry.TryActivateCell(query);
        stopwatch.Stop();

        return new CellResult
        {
            Activated = cellResult.Activated,
            Response = cellResult.Response,
            Confidence = cellResult.Confidence,
            LatencyMs = cellResult.LatencyMs > 0 ? cellResult.LatencyMs : (float)stopwatch.Elapsed.TotalMilliseconds,
            Source = "slow",
            Domain = domain
        };
    }

    // ==================== 协同决策 ====================

    private CellResult CombineFastSlowResults(
        CellResult fastResult, CellResult slowResult, string domain)
    {
        // 如果只有一个激活，直接返回
        if (fastResult.Activated && !slowResult.Activated)
        {
            return fastResult with { Source = "fast" };
        }
        if (!fastResult.Activated && slowResult.Activated)
        {
            return slowResult with { Source = "slow" };
        }
        if (!fastResult.Activated && !slowResult.Activated)
        {
            return new CellResult
            {
                Activated = false,
                Source = "none",
                Domain = domain
            };
        }

        // 两者都激活，加权组合
        var fastScore = fastResult.Confidence * _config.FastWeight;
        var slowScore = slowResult.Confidence * _config.SlowWeight;

        if (fastScore >= slowScore)
        {
            return fastResult with
            {
                Confidence = (fastResult.Confidence * _config.FastWeight + slowResult.Confidence * _config.SlowWeight),
                Source = "combined_fast"
            };
        }
        else
        {
            return slowResult with
            {
                Confidence = (fastResult.Confidence * _config.FastWeight + slowResult.Confidence * _config.SlowWeight),
                Source = "combined_slow"
            };
        }
    }

    // ==================== 交互记录 ====================

    private void RecordInteraction(string query, CellResult result, string domain)
    {
        _totalInteractions++;

        var interaction = new InteractionResult
        {
            Query = query,
            Response = result.Response,
            Domain = domain,
            WasSuccessful = result.Activated && result.Confidence > 0.7f,
            Reward = result.Confidence,
            ReasoningSteps = new List<string> { $"Source: {result.Source}" }
        };

        _interactionBuffer.Enqueue(interaction);

        // 同时存储到双记忆系统
        _memoryStore.StoreEpisode(new RawEpisode
        {
            Query = query,
            FinalAnswer = result.Response,
            Domain = domain,
            WasSuccessful = interaction.WasSuccessful,
            Reward = result.Confidence,
            FullTrajectory = $"Query: {query}\nResponse: {result.Response}\nSource: {result.Source}"
        });

        _logger.LogDebug(
            "Interaction recorded: total={Total} domain={Domain} success={Success} confidence={Conf:F2}",
            _totalInteractions, domain, interaction.WasSuccessful, result.Confidence);
    }

    // ==================== 更新检查 ====================

    private async Task CheckAndUpdateAsync(CancellationToken ct)
    {
        // Fast update: 每 N 次交互更新快速上下文
        if (_totalInteractions % _config.FastUpdateInterval == 0)
        {
            await UpdateFastContextsAsync(ct);
        }

        // Slow update: 每 M 次交互更新慢速模型
        if (_totalInteractions % _config.SlowUpdateInterval == 0)
        {
            await UpdateSlowModelsAsync(ct);
        }

        // Co-evolution: 快速上下文指导慢速训练
        if (_config.EnableCoEvolution && _totalInteractions % (_config.SlowUpdateInterval * 2) == 0)
        {
            await CoEvolveAsync(ct);
        }
    }

    // ==================== Fast Context 更新 ====================

    private async Task UpdateFastContextsAsync(CancellationToken ct)
    {
        var interactions = DrainInteractionBuffer();
        if (interactions.Count == 0) return;

        _logger.LogInformation("Updating fast contexts: interactions={Count}", interactions.Count);

        // 按领域分组
        var byDomain = interactions.GroupBy(i => i.Domain).ToList();

        foreach (var domainGroup in byDomain)
        {
            if (ct.IsCancellationRequested) break;

            var domain = domainGroup.Key;
            var domainInteractions = domainGroup.ToList();

            // 使用 GEPA 优化提示词
            var existingCandidates = GetExistingFastContexts(domain)
                .Select(c => new PromptCandidate
                {
                    Id = c.Id,
                    Domain = c.Domain,
                    Prompt = c.Prompt,
                    Lessons = c.Lessons,
                    Accuracy = c.Accuracy,
                    CreatedAt = c.CreatedAt,
                    IsParetoOptimal = false
                })
                .ToList();

            var optimizedCandidates = await _promptOptimizer.OptimizeAsync(
                domain,
                domainInteractions,
                existingCandidates,
                ct);

            // 更新快速上下文
            foreach (var candidate in optimizedCandidates)
            {
                var context = new FastContext
                {
                    Id = candidate.Id,
                    Domain = candidate.Domain,
                    Prompt = candidate.Prompt,
                    Lessons = candidate.Lessons,
                    Accuracy = candidate.Accuracy,
                    UsageCount = 0,
                    CreatedAt = candidate.CreatedAt,
                    LastUpdated = DateTime.UtcNow,
                    IsFrozen = false
                };

                _fastContexts.AddOrUpdate(
                    context.Id,
                    context,
                    (key, old) => context with { UsageCount = old.UsageCount });
            }

            // 维护 Pareto 前沿（保留最好的 N 个）
            PruneFastContexts(domain);
        }

        _fastUpdates++;
        _lastFastUpdate = DateTime.UtcNow;

        _logger.LogInformation(
            "Fast contexts updated: total={Total} updates={Updates}",
            _fastContexts.Count, _fastUpdates);
    }

    // ==================== Slow Model 更新 ====================

    private async Task UpdateSlowModelsAsync(CancellationToken ct)
    {
        // 收集足够的训练样本
        var samples = _memoryStore.GetUnconsolidatedEpisodes(_config.MinSamplesForSlowTraining * 2);
        if (samples.Count < _config.MinSamplesForSlowTraining)
        {
            _logger.LogDebug(
                "Insufficient samples for slow training: {Count}/{Min}",
                samples.Count, _config.MinSamplesForSlowTraining);
            return;
        }

        _logger.LogInformation("Updating slow models: samples={Count}", samples.Count);

        // 按领域分组训练
        var byDomain = samples.GroupBy(e => e.Domain).ToList();

        foreach (var domainGroup in byDomain)
        {
            if (ct.IsCancellationRequested) break;

            var domain = domainGroup.Key;
            var domainSamples = domainGroup.ToList();

            // 触发 Cell AI 训练
            var success = await _cellRegistry.TrainCellAsync(domain, ct);
            if (success)
            {
                _slowUpdates++;
                _lastSlowUpdate = DateTime.UtcNow;

                _logger.LogInformation(
                    "Slow model trained: domain={Domain} samples={Samples}",
                    domain, domainSamples.Count);
            }
        }
    }

    // ==================== 协同进化 ====================

    private async Task CoEvolveAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting co-evolution cycle...");

        // 1. 使用快速上下文指导慢速训练数据选择
        var fastContexts = _fastContexts.Values.ToList();
        var guidedSamples = SelectGuidedSamples(fastContexts);

        // 2. 使用慢速模型验证快速上下文质量
        var contextQuality = await ValidateContextQualityAsync(fastContexts, ct);

        // 3. 更新低质量上下文
        foreach (var (context, quality) in contextQuality)
        {
            if (quality < 0.5f && !context.IsFrozen)
            {
                _fastContexts.TryRemove(context.Id, out _);
                _logger.LogDebug("Low quality context removed: id={Id} quality={Quality:F2}",
                    context.Id, quality);
            }
        }

        _logger.LogInformation("Co-evolution cycle completed");
    }

    // ==================== 辅助方法 ====================

    private List<InteractionResult> DrainInteractionBuffer()
    {
        var interactions = new List<InteractionResult>();
        while (_interactionBuffer.TryDequeue(out var interaction))
        {
            interactions.Add(interaction);
        }
        return interactions;
    }

    private List<FastContext> GetRelevantFastContexts(string domain)
    {
        return _fastContexts.Values
            .Where(c => c.Domain == domain && !c.IsFrozen)
            .OrderByDescending(c => c.Accuracy)
            .ThenByDescending(c => c.UsageCount)
            .Take(3)
            .ToList();
    }

    private List<FastContext> GetExistingFastContexts(string domain)
    {
        return _fastContexts.Values
            .Where(c => c.Domain == domain)
            .ToList();
    }

    private string BuildEnhancedQuery(string query, List<FastContext> contexts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(query);
        sb.AppendLine();
        sb.AppendLine("## Relevant Context:");
        
        foreach (var ctx in contexts.Take(2))
        {
            sb.AppendLine($"- {ctx.Prompt}");
            if (ctx.Examples.Count > 0)
            {
                sb.AppendLine($"  Example: {ctx.Examples[0]}");
            }
            if (ctx.Lessons.Count > 0)
            {
                sb.AppendLine($"  Lesson: {ctx.Lessons[0]}");
            }
        }

        return sb.ToString();
    }

    private void PruneFastContexts(string domain)
    {
        var domainContexts = _fastContexts.Values
            .Where(c => c.Domain == domain)
            .OrderByDescending(c => c.Accuracy)
            .ThenByDescending(c => c.UsageCount)
            .ToList();

        if (domainContexts.Count > _config.MaxFastContexts)
        {
            var toRemove = domainContexts.Skip(_config.MaxFastContexts);
            foreach (var ctx in toRemove)
            {
                _fastContexts.TryRemove(ctx.Id, out _);
            }
        }
    }

    private List<RawEpisode> SelectGuidedSamples(List<FastContext> fastContexts)
    {
        var guidedSamples = new List<RawEpisode>();

        foreach (var context in fastContexts)
        {
            var samples = _memoryStore.GetEpisodesByDomain(context.Domain, limit: 20);
            guidedSamples.AddRange(samples);
        }

        return guidedSamples.Distinct().ToList();
    }

    private async Task<List<(FastContext Context, float Quality)>> ValidateContextQualityAsync(
        List<FastContext> contexts, CancellationToken ct)
    {
        var qualityResults = new List<(FastContext, float)>();

        foreach (var context in contexts)
        {
            if (ct.IsCancellationRequested) break;

            // 使用上下文处理测试查询
            var testEpisodes = _memoryStore.GetEpisodesByDomain(context.Domain, limit: 5);
            if (testEpisodes.Count == 0) continue;

            var successCount = 0;
            foreach (var episode in testEpisodes)
            {
                var result = await ProcessWithFastContextAsync(episode.Query, context.Domain, ct);
                if (result.Activated && result.Confidence > 0.7f)
                {
                    successCount++;
                }
            }

            var quality = testEpisodes.Count > 0 ? (float)successCount / testEpisodes.Count : 0f;
            qualityResults.Add((context, quality));
        }

        return qualityResults;
    }

    // ==================== 统计信息 ====================

    public FastSlowStats GetStats()
    {
        return new FastSlowStats
        {
            TotalInteractions = _totalInteractions,
            FastContextCount = _fastContexts.Count,
            SlowModelCount = _slowModels.Count,
            FastUpdates = _fastUpdates,
            SlowUpdates = _slowUpdates,
            LastFastUpdate = _lastFastUpdate,
            LastSlowUpdate = _lastSlowUpdate,
            BufferSize = _interactionBuffer.Count,
            FastContexts = _fastContexts.Values.Select(c => new
            {
                c.Id,
                c.Domain,
                c.Accuracy,
                c.UsageCount,
                c.IsFrozen
            }).ToList(),
            SlowModels = _slowModels.Values.Select(m => new
            {
                m.Id,
                m.Domain,
                m.Accuracy,
                m.TrainingSamples,
                m.IsActive
            }).ToList()
        };
    }

    public void Dispose()
    {
        _logger.LogInformation("FastSlowCellAI disposed");
    }
}

public sealed record FastSlowStats
{
    public int TotalInteractions { get; init; }
    public int FastContextCount { get; init; }
    public int SlowModelCount { get; init; }
    public int FastUpdates { get; init; }
    public int SlowUpdates { get; init; }
    public DateTime LastFastUpdate { get; init; }
    public DateTime LastSlowUpdate { get; init; }
    public int BufferSize { get; init; }
    public object FastContexts { get; init; } = new();
    public object SlowModels { get; init; } = new();
}
