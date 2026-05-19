using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Resilience;

public enum NetworkTier
{
    Full,
    Degraded,
    Minimal,
    Offline
}

public sealed record NetworkHealth
{
    [JsonPropertyName("tier")]
    public NetworkTier Tier { get; init; }

    [JsonPropertyName("latency_ms")]
    public double LatencyMs { get; init; }

    [JsonPropertyName("packet_loss_pct")]
    public double PacketLossPct { get; init; }

    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; init; }

    [JsonPropertyName("estimated_recovery_seconds")]
    public int EstimatedRecoverySeconds { get; init; }

    [JsonPropertyName("last_probe")]
    public DateTime LastProbe { get; init; } = DateTime.UtcNow;
}

public sealed record PredictiveCache
{
    [JsonPropertyName("predicted_queries")]
    public List<string> PredictedQueries { get; init; } = new();

    [JsonPropertyName("pre_cached_knowledge")]
    public List<string> PreCachedKnowledge { get; init; } = new();

    [JsonPropertyName("pre_warmed_models")]
    public List<string> PreWarmedModels { get; init; } = new();

    [JsonPropertyName("cached_responses")]
    public int CachedResponses { get; init; }

    [JsonPropertyName("last_prediction")]
    public DateTime? LastPrediction { get; init; }
}

public sealed class ResilienceBrain
{
    private static readonly Lazy<ResilienceBrain> _instance = new(() =>
        new ResilienceBrain(NullLoggerFactory.Instance.CreateLogger<ResilienceBrain>()));

    public static ResilienceBrain Instance => _instance.Value;

    private readonly ILogger<ResilienceBrain> _logger;
    private readonly ConcurrentDictionary<string, (int failures, DateTime lastFail, DateTime trippedAt, bool isOpen)> _breakers = new();
    private readonly HttpClient _httpClient;

    private static readonly (string name, string url)[] ProbeTargets =
    [
        ("deepseek", "https://api.deepseek.com/v1/models"),
        ("github", "https://github.com"),
        ("models", "https://models.dev")
    ];

    private volatile NetworkTier _healthy = NetworkTier.Full;

    private readonly List<NetworkHealth> _healthHistory = new();
    private readonly object _healthLock = new();

    private PredictiveCache _predictiveCache = new();
    private readonly object _cacheLock = new();

    private const int FailureThreshold = 5;
    private const int RecoverySeconds = 60;
    private const int MaxHealthHistory = 50;

    public ResilienceBrain(ILogger<ResilienceBrain> logger)
        : this(logger, new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    public ResilienceBrain(ILogger<ResilienceBrain> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public static void Initialize(ILogger<ResilienceBrain> logger, HttpClient? httpClient = null)
    {
        var instance = httpClient != null
            ? new ResilienceBrain(logger, httpClient)
            : new ResilienceBrain(logger);
        typeof(ResilienceBrain)
            .GetField("_instance", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic)?
            .SetValue(null, new Lazy<ResilienceBrain>(() => instance));
    }

    public void RecordFailure(string service)
    {
        var entry = _breakers.AddOrUpdate(service,
            _ => (1, DateTime.UtcNow, DateTime.MinValue, false),
            (_, existing) =>
            {
                int newFailures = existing.failures + 1;
                bool shouldOpen = newFailures >= FailureThreshold;
                if (shouldOpen && !existing.isOpen)
                {
                    _logger.LogWarning("Circuit breaker OPEN for {Service} after {Failures} failures", service, newFailures);
                }
                return (newFailures, DateTime.UtcNow, shouldOpen ? DateTime.UtcNow : existing.trippedAt, shouldOpen);
            });
    }

    public void RecordSuccess(string service)
    {
        _breakers.AddOrUpdate(service,
            _ => (0, DateTime.MinValue, DateTime.MinValue, false),
            (_, existing) =>
            {
                if (existing.isOpen)
                    _logger.LogInformation("Circuit breaker CLOSED for {Service}", service);
                return (0, DateTime.UtcNow, DateTime.MinValue, false);
            });
    }

    public bool IsOpen(string service)
    {
        if (!_breakers.TryGetValue(service, out var entry))
            return false;

        if (!entry.isOpen)
            return false;

        bool recovered = (DateTime.UtcNow - entry.trippedAt).TotalSeconds > RecoverySeconds;
        if (recovered)
        {
            _breakers.TryUpdate(service,
                (0, entry.lastFail, DateTime.MinValue, false),
                entry);
            _logger.LogInformation("Circuit breaker auto-recovered for {Service}", service);
            return false;
        }

        return true;
    }

    public async Task<NetworkHealth> ProbeHealthAsync()
    {
        double totalLatency = 0;
        int failures = 0;
        int probesSent = ProbeTargets.Length;
        var probeStart = DateTime.UtcNow;

        var tasks = ProbeTargets.Select(async target =>
        {
            try
            {
                var sw = global::System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(target.url, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Probe {Target} OK: {Latency}ms", target.name, sw.ElapsedMilliseconds);
                    return (latency: (double)sw.ElapsedMilliseconds, success: true);
                }
                else
                {
                    _logger.LogWarning("Probe {Target} returned {Status}", target.name, (int)response.StatusCode);
                    return (latency: (double)sw.ElapsedMilliseconds, success: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Probe {Target} failed", target.name);
                return (latency: 0.0, success: false);
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.success)
                totalLatency += result.latency;
            else
                failures++;
        }

        double packetLossPct = probesSent > 0 ? (double)failures / probesSent * 100 : 100;
        double avgLatency = (probesSent - failures) > 0 ? totalLatency / (probesSent - failures) : 0;

        NetworkTier tier;
        if (failures == probesSent)
            tier = NetworkTier.Offline;
        else if (packetLossPct > 50)
            tier = NetworkTier.Minimal;
        else if (packetLossPct > 20)
            tier = NetworkTier.Degraded;
        else
            tier = NetworkTier.Full;

        _healthy = tier;

        int consecutiveFails = _breakers.Values.Sum(b => b.failures);
        int estimatedRecovery = tier switch
        {
            NetworkTier.Full => 0,
            NetworkTier.Degraded => 30,
            NetworkTier.Minimal => 120,
            NetworkTier.Offline => 300,
            _ => 0
        };

        var health = new NetworkHealth
        {
            Tier = tier,
            LatencyMs = Math.Round(avgLatency, 1),
            PacketLossPct = Math.Round(packetLossPct, 1),
            ConsecutiveFailures = consecutiveFails,
            EstimatedRecoverySeconds = estimatedRecovery,
            LastProbe = probeStart
        };

        lock (_healthLock)
        {
            _healthHistory.Add(health);
            if (_healthHistory.Count > MaxHealthHistory)
            {
                _healthHistory.RemoveAt(0);
            }
        }

        _logger.LogInformation("Health probe complete: Tier={Tier}, Loss={Loss}%, Latency={Latency}ms",
            tier, packetLossPct, avgLatency);

        return health;
    }

    public NetworkHealth GetHealth()
    {
        lock (_healthLock)
        {
            var last = _healthHistory.LastOrDefault();
            if (last != null)
                return last;
        }

        return new NetworkHealth
        {
            Tier = _healthy,
            LatencyMs = 0,
            PacketLossPct = 0,
            ConsecutiveFailures = 0,
            EstimatedRecoverySeconds = 0,
            LastProbe = DateTime.UtcNow
        };
    }

    public NetworkTier GetTier()
    {
        return _healthy;
    }

    public List<string> PredictQueries(string context)
    {
        var keywords = context.Split(' ', ',', '.', '，', '。')
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 2)
            .Distinct()
            .ToList();

        var templates = new List<string>
        {
            $"分析 {keywords.FirstOrDefault() ?? "数据"} 趋势",
            $"生成 {keywords.ElementAtOrDefault(1) ?? keywords.FirstOrDefault() ?? "系统"} 报告",
            $"优化 {keywords.ElementAtOrDefault(2) ?? keywords.FirstOrDefault() ?? "性能"} 参数",
            $"查询 {keywords.ElementAtOrDefault(1) ?? "当前"} 状态",
            $"预测 {keywords.FirstOrDefault() ?? "下一阶段"} 发展"
        };

        var predicted = templates.Take(Math.Min(5, templates.Count)).ToList();
        var rng = Random.Shared;
        int count = rng.Next(3, Math.Min(6, predicted.Count + 1));

        var result = predicted.OrderBy(_ => rng.Next()).Take(count).ToList();

        lock (_cacheLock)
        {
            _predictiveCache = _predictiveCache with
            {
                PredictedQueries = result,
                LastPrediction = DateTime.UtcNow
            };
        }

        _logger.LogInformation("Predicted {Count} queries from context", result.Count);
        return result;
    }

    public void PreCacheForOffline(IEnumerable<string> knowledgeItems)
    {
        var items = knowledgeItems.ToList();

        lock (_cacheLock)
        {
            foreach (var item in items)
            {
                if (!_predictiveCache.PreCachedKnowledge.Contains(item))
                {
                    _predictiveCache.PreCachedKnowledge.Add(item);
                }
            }
            _predictiveCache = _predictiveCache with
            {
                CachedResponses = _predictiveCache.PreCachedKnowledge.Count
            };
        }

        _logger.LogInformation("Pre-cached {Count} knowledge items for offline", items.Count);
    }

    public void PreWarmModel(string modelName)
    {
        lock (_cacheLock)
        {
            if (!_predictiveCache.PreWarmedModels.Contains(modelName))
            {
                _predictiveCache.PreWarmedModels.Add(modelName);
            }
        }

        _logger.LogInformation("Pre-warmed model: {Model}", modelName);
    }

    public Dictionary<string, object> Stats()
    {
        int healthHistorySize;
        lock (_healthLock)
        {
            healthHistorySize = _healthHistory.Count;
        }

        return new Dictionary<string, object>
        {
            ["tier"] = _healthy.ToString(),
            ["breaker_count"] = _breakers.Count,
            ["open_breakers"] = _breakers.Values.Count(b => b.isOpen),
            ["health_history_size"] = healthHistorySize,
            ["probe_targets"] = ProbeTargets.Length,
            ["failure_threshold"] = FailureThreshold,
            ["recovery_seconds"] = RecoverySeconds
        };
    }

    public PredictiveCache GetCache()
    {
        lock (_cacheLock)
        {
            return _predictiveCache;
        }
    }
}
