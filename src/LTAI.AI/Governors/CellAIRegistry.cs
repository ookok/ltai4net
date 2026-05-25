using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum CellState { Dormant, Active, Learning, Mature, Degraded }

public sealed record CellModelInfo
{
    public string Domain { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public CellState State { get; init; }
    public int SampleCount { get; init; }
    public float Accuracy { get; init; }
    public DateTime LastUsed { get; init; }
    public DateTime LastTrained { get; init; }
    public long SizeBytes { get; init; }
    public int ActivationCount { get; init; }
    public float AvgLatencyMs { get; init; }
    public int Version { get; init; }
    public List<string> VersionHistory { get; init; } = new();
}

public sealed record CellRule
{
    public string Domain { get; init; } = "";
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public int MinSamplesToTrain { get; init; } = 20;
    public float MinAccuracyToActivate { get; init; } = 0.6f;
    public float MaxLatencyMs { get; init; } = 5.0f;
    public int MaxMemoryMB { get; init; } = 50;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(24);
    public bool AutoTrain { get; init; } = true;
    public int Priority { get; init; } = 0;
    public float MinConfidenceThreshold { get; init; } = 0.5f;
}

public sealed record CellActivationResult
{
    public bool Activated { get; init; }
    public string Domain { get; init; } = "";
    public string Response { get; init; } = "";
    public float Confidence { get; init; }
    public float LatencyMs { get; init; }
    public CellModelInfo? CellInfo { get; init; }
}

public sealed class CellAIRegistry
{
    private readonly Dictionary<string, CellRule> _rules = new();
    private readonly Dictionary<string, CellModelInfo> _cells = new();
    private readonly Dictionary<string, SynapticInference> _engines = new();
    private readonly Dictionary<string, ICellEngine> _pretrainedEngines = new();
    private readonly CellAnswerStore _answerStore;
    private readonly SynapticTrainer _trainer;
    private readonly SynapticMemory _memory;
    private readonly ILogger<CellAIRegistry> _logger;
    private readonly string _cellDirectory;
    private readonly string _pretrainedDirectory;
    private readonly object _lock = new();
    private long _totalActivations;
    private long _totalMemoryBytes;
    private float _selfTrainedOverrideThreshold = 0.75f;
    private bool _fallbackToSelfTrained = true;

    public CellAIRegistry(
        CellAnswerStore answerStore,
        SynapticTrainer trainer,
        SynapticMemory memory,
        ILogger<CellAIRegistry>? logger = null)
    {
        _answerStore = answerStore;
        _trainer = trainer;
        _memory = memory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CellAIRegistry>.Instance;
        _cellDirectory = Path.Combine(AppContext.BaseDirectory, "synaptic", "cells");
        _pretrainedDirectory = Path.Combine(AppContext.BaseDirectory, "synaptic", "pretrained");
        Directory.CreateDirectory(_cellDirectory);
        Directory.CreateDirectory(_pretrainedDirectory);

        InitializeDefaultRules();
        InitializeSeedEngines();  // ← 加载内置种子
    }

    private void InitializeSeedEngines()
    {
        var seeds = SeedModelFactory.CreateDefaultSeeds();
        foreach (var (domain, engine) in seeds)
        {
            // 将规则引擎包装为 OnnxCellEngine 兼容的接口
            // 这里我们直接使用 RuleBasedCellEngine，并在 SelectBestModelAndPredict 中处理
            // 为简化，我们将其注册为特殊的“预训练引擎”
            _pretrainedEngines[domain] = new RuleEngineAdapter(engine);
            
            _logger.LogDebug("Seed engine registered: domain={Domain}", domain);
        }
    }

    public void ConfigureHybridStrategy(float selfTrainedOverrideThreshold = 0.75f, bool fallbackToSelfTrained = true)
    {
        _selfTrainedOverrideThreshold = selfTrainedOverrideThreshold;
        _fallbackToSelfTrained = fallbackToSelfTrained;
        _logger.LogInformation(
            "Hybrid strategy configured: overrideThreshold={Threshold:F2} fallback={Fallback}",
            _selfTrainedOverrideThreshold, _fallbackToSelfTrained);
    }

    private void InitializeDefaultRules()
    {
        var defaultRules = new[]
        {
            new CellRule
            {
                Domain = "code",
                Keywords = new[] { "code", "function", "class", "method", "编程", "代码", "函数", "bug", "debug", "api", "algorithm" },
                MinSamplesToTrain = 30,
                MinAccuracyToActivate = 0.65f,
                MaxLatencyMs = 3.0f,
                MaxMemoryMB = 100,
                Priority = 10
            },
            new CellRule
            {
                Domain = "math",
                Keywords = new[] { "calculate", "equation", "formula", "math", "计算", "公式", "数学", "求解", "积分", "导数" },
                MinSamplesToTrain = 20,
                MinAccuracyToActivate = 0.7f,
                MaxLatencyMs = 2.0f,
                MaxMemoryMB = 30,
                Priority = 9
            },
            new CellRule
            {
                Domain = "science",
                Keywords = new[] { "physics", "chemistry", "biology", "物理", "化学", "生物", "科学", "实验", "理论" },
                MinSamplesToTrain = 25,
                MinAccuracyToActivate = 0.6f,
                MaxLatencyMs = 5.0f,
                MaxMemoryMB = 50,
                Priority = 8
            },
            new CellRule
            {
                Domain = "language",
                Keywords = new[] { "translate", "grammar", "word", "翻译", "语法", "语言", "拼写", "词义", "sentence" },
                MinSamplesToTrain = 20,
                MinAccuracyToActivate = 0.75f,
                MaxLatencyMs = 2.0f,
                MaxMemoryMB = 40,
                Priority = 7
            },
            new CellRule
            {
                Domain = "system",
                Keywords = new[] { "system", "config", "setup", "install", "系统", "配置", "安装", "error", "log", "service" },
                MinSamplesToTrain = 25,
                MinAccuracyToActivate = 0.6f,
                MaxLatencyMs = 4.0f,
                MaxMemoryMB = 60,
                Priority = 6
            },
            new CellRule
            {
                Domain = "creative",
                Keywords = new[] { "write", "story", "poem", "creative", "写", "故事", "诗", "创意", "想象" },
                MinSamplesToTrain = 15,
                MinAccuracyToActivate = 0.5f,
                MaxLatencyMs = 5.0f,
                MaxMemoryMB = 30,
                Priority = 5
            },
            new CellRule
            {
                Domain = "greeting",
                Keywords = new[] { "hello", "hi", "你好", "早上好", "晚上好", "hey", "greetings" },
                MinSamplesToTrain = 10,
                MinAccuracyToActivate = 0.9f,
                MaxLatencyMs = 1.0f,
                MaxMemoryMB = 10,
                Priority = 1
            }
        };

        foreach (var rule in defaultRules)
        {
            _rules[rule.Domain] = rule;
            _cells[rule.Domain] = new CellModelInfo
            {
                Domain = rule.Domain,
                State = CellState.Dormant,
                ModelPath = ""
            };
        }

        _logger.LogInformation("CellAIRegistry initialized with {Count} default domains", _rules.Count);
    }

    public async Task InitializePretrainedModelsAsync(
        Dictionary<string, OnnxModelConfig>? models = null,
        bool autoDownload = true,
        CancellationToken ct = default)
    {
        var modelConfigs = models ?? PretrainedModelRegistry.GetDefaultModels(_pretrainedDirectory);

        foreach (var (domain, config) in modelConfigs)
        {
            if (autoDownload)
            {
                await PretrainedModelRegistry.DownloadModelAsync(config, _pretrainedDirectory, _logger, ct).ConfigureAwait(false);
            }

            if (!File.Exists(config.ModelPath))
            {
                _logger.LogWarning("Pretrained model not available: {Domain}", domain);
                continue;
            }

            var engine = new OnnxCellEngine(config);
            if (await engine.LoadAsync(ct))
            {
                lock (_lock)
                {
                    _pretrainedEngines[domain] = engine;
                    
                    if (_cells.TryGetValue(domain, out var cell))
                    {
                        _cells[domain] = cell with
                        {
                            State = cell.State == CellState.Dormant ? CellState.Mature : cell.State,
                            ModelPath = config.ModelPath,
                            SizeBytes = new FileInfo(config.ModelPath).Length
                        };
                    }
                }

                _logger.LogInformation("Pretrained model loaded: {Domain} source={Source}", domain, config.Source);
            }
        }

        _logger.LogInformation(
            "Pretrained models initialized: {Count} models loaded", _pretrainedEngines.Count);
    }

    public void AddRule(CellRule rule)
    {
        lock (_lock)
        {
            _rules[rule.Domain] = rule;
            if (!_cells.ContainsKey(rule.Domain))
            {
                _cells[rule.Domain] = new CellModelInfo
                {
                    Domain = rule.Domain,
                    State = CellState.Dormant,
                    ModelPath = ""
                };
            }
            
            // 如果是动态注册的新领域，自动初始化种子引擎
            if (!_pretrainedEngines.ContainsKey(rule.Domain))
            {
                var seedEngine = new RuleBasedCellEngine(rule.Domain, new List<RuleMapping>
                {
                    new() { Keywords = rule.Keywords, Label = rule.Domain, Confidence = 0.5f }
                });
                _pretrainedEngines[rule.Domain] = new RuleEngineAdapter(seedEngine);
                _logger.LogInformation("Auto-initialized seed engine for new domain: {Domain}", rule.Domain);
            }
        }
        _logger.LogInformation("Cell rule added: {Domain}", rule.Domain);
    }

    /// <summary>
    /// 注册动态发现的新领域
    /// </summary>
    public void RegisterDynamicDomain(string domain, string[] keywords)
    {
        var rule = new CellRule
        {
            Domain = domain,
            Keywords = keywords,
            MinSamplesToTrain = 15,
            MinAccuracyToActivate = 0.5f,
            MaxMemoryMB = 30,
            Priority = 1,
            AutoTrain = true
        };

        AddRule(rule);
    }

    public string DetectDomain(string query)
    {
        var lower = query.ToLowerInvariant();
        var scored = _rules.Select(r => new
        {
            Domain = r.Key,
            Score = r.Value.Keywords.Count(kw => lower.Contains(kw))
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => _rules[x.Domain].Priority)
        .FirstOrDefault();

        return scored?.Domain ?? "general";
    }

    public CellActivationResult TryActivateCell(string query)
    {
        var domain = DetectDomain(query);
        if (domain == "general")
            return new CellActivationResult { Activated = false, Domain = "general" };

        var rule = _rules[domain];
        var answerResult = _answerStore.FindAnswer(domain, query);
        if (answerResult.Found && answerResult.Confidence >= rule.MinConfidenceThreshold)
        {
            _totalActivations++;
            lock (_lock)
            {
                if (_cells.TryGetValue(domain, out var cell))
                {
                    _cells[domain] = cell with
                    {
                        LastUsed = DateTime.UtcNow,
                        ActivationCount = cell.ActivationCount + 1
                    };
                }
            }

            return new CellActivationResult
            {
                Activated = true,
                Domain = domain,
                Response = answerResult.Answer,
                Confidence = answerResult.Confidence,
                LatencyMs = 0.1f,
                CellInfo = _cells.GetValueOrDefault(domain)
            };
        }

        if (answerResult.Found && answerResult.Confidence < rule.MinConfidenceThreshold)
        {
            _logger.LogDebug("Cell answer below confidence threshold: domain={Domain}, confidence={Conf:F2} < {Min:F2}",
                domain, answerResult.Confidence, rule.MinConfidenceThreshold);
        }

        // 混合策略：选择最佳模型
        return SelectBestModelAndPredict(query, domain, rule);
    }

    private CellActivationResult SelectBestModelAndPredict(string query, string domain, CellRule rule)
    {
        CellModelInfo? cellInfo;
        bool hasSelfTrained = false;
        bool hasPretrained = false;

        lock (_lock)
        {
            if (_cells.TryGetValue(domain, out var cell))
            {
                hasSelfTrained = cell.State == CellState.Active && _engines.ContainsKey(domain);
                hasPretrained = _pretrainedEngines.ContainsKey(domain);
                cellInfo = cell;
            }
            else
            {
                cellInfo = null;
            }
        }

        // 决策逻辑：
        // 1. 如果自训练模型准确率超过阈值，优先使用自训练模型
        // 2. 否则使用预训练模型（冷启动）
        // 3. 如果都没有，返回未激活

        if (hasSelfTrained && cellInfo!.Accuracy >= _selfTrainedOverrideThreshold)
        {
            _logger.LogDebug("Using self-trained model: {Domain} accuracy={Accuracy:F2}", domain, cellInfo.Accuracy);
            return PredictWithSelfTrainedModel(query, domain, cellInfo, rule);
        }

        if (hasPretrained)
        {
            _logger.LogDebug("Using pretrained ONNX model: {Domain}", domain);
            return PredictWithPretrainedModel(query, domain, rule);
        }

        if (hasSelfTrained && _fallbackToSelfTrained)
        {
            _logger.LogDebug("Falling back to self-trained model: {Domain} accuracy={Accuracy:F2}", domain, cellInfo!.Accuracy);
            return PredictWithSelfTrainedModel(query, domain, cellInfo, rule);
        }

        return new CellActivationResult { Activated = false, Domain = domain };
    }

    private CellActivationResult PredictWithSelfTrainedModel(
        string query, string domain, CellModelInfo cellInfo, CellRule rule)
    {
        SynapticInference? engine;
        lock (_lock)
        {
            if (!_engines.TryGetValue(domain, out engine) || !engine.IsReady)
                return new CellActivationResult { Activated = false, Domain = domain };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = engine.Predict(query);
        stopwatch.Stop();

        if (result.Confidence < rule.MinConfidenceThreshold)
        {
            _logger.LogDebug("Self-trained inference below threshold: domain={Domain} confidence={Conf:F2}",
                domain, result.Confidence);
            return new CellActivationResult { Activated = false, Domain = domain, Confidence = result.Confidence };
        }

        _totalActivations++;
        lock (_lock)
        {
            _cells[domain] = cellInfo with
            {
                LastUsed = DateTime.UtcNow,
                ActivationCount = cellInfo.ActivationCount + 1,
                AvgLatencyMs = (cellInfo.AvgLatencyMs * (cellInfo.ActivationCount - 1) + (float)stopwatch.Elapsed.TotalMilliseconds) / cellInfo.ActivationCount
            };
        }

        return new CellActivationResult
        {
            Activated = true,
            Domain = domain,
            Response = result.PredictedLabel,
            Confidence = result.Confidence,
            LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds,
            CellInfo = _cells.GetValueOrDefault(domain)
        };
    }

    private CellActivationResult PredictWithPretrainedModel(
        string query, string domain, CellRule rule)
    {
        ICellEngine? engine;
        lock (_lock)
        {
            if (!_pretrainedEngines.TryGetValue(domain, out engine) || !engine.IsReady)
                return new CellActivationResult { Activated = false, Domain = domain };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = engine.Predict(query);
        stopwatch.Stop();

        if (result.Confidence < rule.MinConfidenceThreshold)
        {
            _logger.LogDebug("Pretrained inference below threshold: domain={Domain} confidence={Conf:F2}",
                domain, result.Confidence);
            return new CellActivationResult { Activated = false, Domain = domain, Confidence = result.Confidence };
        }

        _totalActivations++;
        lock (_lock)
        {
            if (_cells.TryGetValue(domain, out var cell))
            {
                _cells[domain] = cell with
                {
                    LastUsed = DateTime.UtcNow,
                    ActivationCount = cell.ActivationCount + 1,
                    AvgLatencyMs = (cell.AvgLatencyMs * (cell.ActivationCount - 1) + (float)stopwatch.Elapsed.TotalMilliseconds) / cell.ActivationCount
                };
            }
        }

        return new CellActivationResult
        {
            Activated = true,
            Domain = domain,
            Response = result.PredictedLabel,
            Confidence = result.Confidence,
            LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds,
            CellInfo = _cells.GetValueOrDefault(domain)
        };
    }

    public async Task<bool> TrainCellAsync(string domain, CancellationToken ct = default)
    {
        if (!_rules.TryGetValue(domain, out var rule))
        {
            _logger.LogWarning("Cannot train cell: unknown domain {Domain}", domain);
            return false;
        }

        var samples = _memory.GetSamplesByDomain(domain, rule.MinSamplesToTrain * 2);
        if (samples.Count < rule.MinSamplesToTrain)
        {
            _logger.LogInformation("Insufficient samples for {Domain}: {Count}/{Min}",
                domain, samples.Count, rule.MinSamplesToTrain);
            return false;
        }

        var domainDir = Path.Combine(_cellDirectory, domain);
        Directory.CreateDirectory(domainDir);

        var oldTrainer = new SynapticTrainer(domainDir);
        var result = oldTrainer.TrainIntentClassifier(samples);

        if (!result.Success)
        {
            _logger.LogWarning("Cell training failed for {Domain}: {Error}", domain, result.ErrorMessage);
            return false;
        }

        if (result.Accuracy < rule.MinAccuracyToActivate)
        {
            _logger.LogInformation("Cell accuracy too low for activation: {Domain} accuracy={Accuracy:F2} < {Min:F2}",
                domain, result.Accuracy, rule.MinAccuracyToActivate);

            lock (_lock)
            {
                _cells[domain] = _cells[domain] with
                {
                    State = CellState.Learning,
                    ModelPath = result.ModelPath,
                    Accuracy = result.Accuracy,
                    SampleCount = samples.Count,
                    LastTrained = DateTime.UtcNow
                };
            }
            return false;
        }

        var fileInfo = new FileInfo(result.ModelPath);
        var cellInfo = new CellModelInfo
        {
            Domain = domain,
            ModelPath = result.ModelPath,
            State = CellState.Active,
            SampleCount = samples.Count,
            Accuracy = result.Accuracy,
            LastUsed = DateTime.UtcNow,
            LastTrained = DateTime.UtcNow,
            SizeBytes = fileInfo.Length,
            Version = (_cells.GetValueOrDefault(domain)?.Version ?? 0) + 1,
            VersionHistory = new List<string>(_cells.GetValueOrDefault(domain)?.VersionHistory ?? new()) { result.ModelPath }
        };

        var newEngine = new SynapticInference();
        if (!newEngine.LoadModel(result.ModelPath))
        {
            _logger.LogWarning("Failed to load new model for {Domain}", domain);
            return false;
        }

        lock (_lock)
        {
            var oldEngine = _engines.GetValueOrDefault(domain);
            _engines[domain] = newEngine;

            _cells[domain] = cellInfo;
            _totalMemoryBytes += fileInfo.Length;

            if (oldEngine != null)
            {
                _totalMemoryBytes -= _cells[domain].SizeBytes;
                Task.Run(() =>
                {
                    try
                    {
                        oldEngine.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to dispose old cell engine: {Domain}", domain);
                    }
                });
            }

            _logger.LogInformation(
                "Cell activated: {Domain} state={State} accuracy={Accuracy:F2} samples={Samples} size={SizeKB:F1}KB",
                domain, cellInfo.State, result.Accuracy, samples.Count, fileInfo.Length / 1024.0);

            foreach (var sample in samples.Take(20))
            {
                _answerStore.LearnFromL2(domain, sample.Text, sample.Label, result.Accuracy);
            }
        }

        return true;
    }

    public async Task UnloadIdleCellsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var toUnload = new List<string>();

        lock (_lock)
        {
            foreach (var (domain, cell) in _cells)
            {
                if (cell.State == CellState.Active &&
                    (now - cell.LastUsed) > _rules[domain].IdleTimeout)
                {
                    toUnload.Add(domain);
                }
            }
        }

        foreach (var domain in toUnload)
        {
            lock (_lock)
            {
                if (_engines.Remove(domain, out var engine))
                {
                    engine.Dispose();
                    _totalMemoryBytes -= _cells[domain].SizeBytes;
                    _cells[domain] = _cells[domain] with { State = CellState.Dormant };
                    _logger.LogInformation("Self-trained cell unloaded (idle): {Domain}", domain);
                }
            }
        }

        _logger.LogInformation("Idle cells unloaded: {Count}", toUnload.Count);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var engine in _engines.Values)
            {
                try { engine.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose engine in CellAIRegistry"); }
            }
            _engines.Clear();

            foreach (var engine in _pretrainedEngines.Values)
            {
                try { engine.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose pretrained engine in CellAIRegistry"); }
            }
            _pretrainedEngines.Clear();
        }

        _logger.LogInformation("CellAIRegistry disposed");
    }

    public async Task PruneLowAccuracyCellsAsync(CancellationToken ct = default)
    {
        var toPrune = new List<string>();

        lock (_lock)
        {
            foreach (var (domain, cell) in _cells)
            {
                if (cell.State == CellState.Active && cell.Accuracy < 0.5f)
                {
                    toPrune.Add(domain);
                }
            }
        }

        foreach (var domain in toPrune)
        {
            lock (_lock)
            {
                if (_engines.Remove(domain, out var engine))
                {
                    engine.Dispose();
                    _totalMemoryBytes -= _cells[domain].SizeBytes;
                    _cells[domain] = _cells[domain] with { State = CellState.Degraded };
                    _logger.LogWarning("Cell pruned (low accuracy): {Domain} accuracy={Accuracy:F2}",
                        domain, _cells[domain].Accuracy);
                }
            }
        }
    }

    public Dictionary<string, object> GetMetrics()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_cells"] = _cells.Count,
                ["active_cells"] = _cells.Count(c => c.Value.State == CellState.Active),
                ["learning_cells"] = _cells.Count(c => c.Value.State == CellState.Learning),
                ["mature_cells"] = _cells.Count(c => c.Value.State == CellState.Mature),
                ["dormant_cells"] = _cells.Count(c => c.Value.State == CellState.Dormant),
                ["pretrained_models"] = _pretrainedEngines.Count,
                ["self_trained_models"] = _engines.Count,
                ["total_activations"] = _totalActivations,
                ["total_memory_bytes"] = _totalMemoryBytes,
                ["total_memory_mb"] = _totalMemoryBytes / (1024.0 * 1024.0),
                ["hybrid_strategy"] = new
                {
                    selfTrainedOverrideThreshold = _selfTrainedOverrideThreshold,
                    fallbackToSelfTrained = _fallbackToSelfTrained
                },
                ["cells"] = _cells.ToDictionary(kvp => kvp.Key, kvp => new
                {
                    kvp.Value.Domain,
                    State = kvp.Value.State.ToString(),
                    kvp.Value.Accuracy,
                    kvp.Value.SampleCount,
                    kvp.Value.ActivationCount,
                    kvp.Value.AvgLatencyMs,
                    SizeKB = kvp.Value.SizeBytes / 1024.0,
                    HasPretrained = _pretrainedEngines.ContainsKey(kvp.Key),
                    HasSelfTrained = _engines.ContainsKey(kvp.Key)
                })
            };
        }
    }

    public IReadOnlyDictionary<string, CellModelInfo> GetCells()
    {
        lock (_lock)
        {
            return _cells.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    public CellActivationResult TryCrossDomainActivation(string query)
    {
        var primaryDomain = DetectDomain(query);
        if (primaryDomain != "general")
        {
            var primaryResult = TryActivateCell(query);
            if (primaryResult.Activated)
                return primaryResult;
        }

        var relatedDomains = GetRelatedDomains(primaryDomain);
        foreach (var domain in relatedDomains)
        {
            var rule = _rules.GetValueOrDefault(domain);
            if (rule == null) continue;

            var answerResult = _answerStore.FindAnswer(domain, query);
            if (answerResult.Found && answerResult.Confidence >= rule.MinConfidenceThreshold)
            {
                return new CellActivationResult
                {
                    Activated = true,
                    Domain = domain,
                    Response = answerResult.Answer,
                    Confidence = answerResult.Confidence,
                    LatencyMs = 0.1f
                };
            }

            // 尝试自训练模型
            if (_engines.TryGetValue(domain, out var selfEngine) && selfEngine.IsReady)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = selfEngine.Predict(query);
                stopwatch.Stop();

                if (result.Confidence >= rule.MinConfidenceThreshold)
                {
                    return new CellActivationResult
                    {
                        Activated = true,
                        Domain = domain,
                        Response = result.PredictedLabel,
                        Confidence = result.Confidence,
                        LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds
                    };
                }
            }

            // 尝试预训练模型
            if (_pretrainedEngines.TryGetValue(domain, out var pretrainedEngine) && pretrainedEngine.IsReady)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = pretrainedEngine.Predict(query);
                stopwatch.Stop();

                if (result.Confidence >= rule.MinConfidenceThreshold)
                {
                    return new CellActivationResult
                    {
                        Activated = true,
                        Domain = domain,
                        Response = result.PredictedLabel,
                        Confidence = result.Confidence,
                        LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds
                    };
                }
            }
        }

        return new CellActivationResult { Activated = false, Domain = "cross_domain_failed" };
    }

    private List<string> GetRelatedDomains(string domain)
    {
        var relations = new Dictionary<string, string[]>
        {
            ["code"] = new[] { "math", "system" },
            ["math"] = new[] { "science", "code" },
            ["science"] = new[] { "math", "system" },
            ["language"] = new[] { "creative" },
            ["system"] = new[] { "code" },
            ["creative"] = new[] { "language" },
        };

        return relations.GetValueOrDefault(domain, Array.Empty<string>()).ToList();
    }
}
