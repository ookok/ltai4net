using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Routing;

public sealed class HolisticElection
{
    private readonly ILogger<HolisticElection>? _logger;
    private static readonly Lazy<HolisticElection> _instance = new(() => new HolisticElection());

    public static HolisticElection Instance => _instance.Value;

    public Dictionary<string, double> Weights { get; } = new()
    {
        ["latency"] = 0.18,
        ["quality"] = 0.23,
        ["cost"] = 0.15,
        ["capability"] = 0.12,
        ["freshness"] = 0.05,
        ["rate_limit"] = 0.07,
        ["cache"] = 0.10,
        ["sticky"] = 0.10,
        ["hifloat8"] = 0.0,
        ["elo"] = 0.0,
        ["long_term_reward"] = 0.0,
        ["thompson"] = 0.0,
        ["exploration"] = 0.0,
        ["budget"] = 0.0
    };

    public static readonly Dictionary<string, List<string>> ProviderCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = new() { "code", "reasoning", "analysis" },
        ["openai"] = new() { "multimodal", "code", "reasoning", "vision" },
        ["claude"] = new() { "long_context", "safety", "code", "reasoning", "analysis" },
        ["qwen"] = new() { "chinese", "code", "reasoning" },
        ["moonshot"] = new() { "long_text", "chinese" },
        ["glm"] = new() { "agent", "tools", "chinese", "code" },
        ["gemini"] = new() { "vision", "search", "multimodal", "long_context" },
        ["mistral"] = new() { "code", "multimodal" },
        ["llama"] = new() { "code", "reasoning" },
        ["grok"] = new() { "reasoning", "multimodal" },
        ["deepseek"] = new() { "code", "reasoning", "analysis" },
        ["minimax"] = new() { "chinese", "long_context" },
        ["doubao"] = new() { "chinese", "reasoning" },
        ["stepfun"] = new() { "chinese", "multimodal" },
        ["ernie"] = new() { "chinese", "search", "multimodal" },
        ["hunyuan"] = new() { "chinese", "search" }
    };

    private readonly ConcurrentDictionary<string, RouterStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public HolisticElection(ILogger<HolisticElection>? logger = null)
    {
        _logger = logger;
    }

    public sealed class RouterStats
    {
        private const int MaxWindow = 20;
        private readonly object _lock = new();

        public Queue<bool> RecentSuccesses { get; } = new(MaxWindow);
        public Queue<double> RecentLatencies { get; } = new(MaxWindow);
        public double LastQuality { get; set; } = 0.5;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public DateTime? LastRateLimit { get; set; }
        public int TotalCalls { get; set; }
        public int TotalFailures { get; set; }
        public double CumulativeLatencyMs { get; set; }

        public double SuccessRate
        {
            get
            {
                lock (_lock)
                {
                    if (RecentSuccesses.Count < 3)
                        return 0.5;
                    return (double)RecentSuccesses.Count(s => s) / RecentSuccesses.Count;
                }
            }
        }

        public double AvgLatencyMs
        {
            get
            {
                lock (_lock)
                {
                    if (RecentLatencies.Count == 0)
                        return 0;
                    return RecentLatencies.Average();
                }
            }
        }

        public double RecentQuality
        {
            get
            {
                lock (_lock)
                {
                    var latencies = RecentLatencies.ToArray();
                    if (latencies.Length == 0)
                        return LastQuality;

                    double totalWeight = 0;
                    double weightedSum = 0;
                    for (int i = 0; i < latencies.Length; i++)
                    {
                        double w = (i + 1.0) / latencies.Length;
                        totalWeight += w;
                        weightedSum += w * QualityFromLatency(latencies[i]);
                    }
                    return totalWeight > 0 ? weightedSum / totalWeight : LastQuality;
                }
            }
        }

        public void RecordCall(bool success, double latencyMs, double quality = double.NaN)
        {
            lock (_lock)
            {
                TotalCalls++;
                if (!success) TotalFailures++;

                RecentSuccesses.Enqueue(success);
                if (RecentSuccesses.Count > MaxWindow)
                    RecentSuccesses.Dequeue();

                RecentLatencies.Enqueue(latencyMs);
                if (RecentLatencies.Count > MaxWindow)
                    RecentLatencies.Dequeue();

                CumulativeLatencyMs += latencyMs;
                LastUsed = DateTime.UtcNow;

                if (!double.IsNaN(quality))
                    LastQuality = quality;
            }
        }

        public void RecordRateLimit()
        {
            lock (_lock)
            {
                LastRateLimit = DateTime.UtcNow;
            }
        }

        public ProjectionResult ProjectFuture(double costPer1k, double taskComplexity)
        {
            var adjustedLatency = AvgLatencyMs * (1.0 + taskComplexity * 0.5);
            var quality = RecentQuality;
            var cost = costPer1k * 4.0; // assume 4K tokens
            var riskScore = (1.0 - SuccessRate) * 0.5 + Math.Min(adjustedLatency / 10000.0, 1.0) * 0.5;
            var confidence = Math.Max(0.1, 1.0 - riskScore);

            var tier = confidence switch
            {
                >= 0.9 => "excellent",
                >= 0.7 => "good",
                >= 0.5 => "fair",
                _ => "poor"
            };

            return new ProjectionResult
            {
                PredictedLatencyMs = adjustedLatency,
                PredictedQuality = quality,
                PredictedCost = cost,
                RiskScore = riskScore,
                Confidence = confidence,
                RecommendationTier = tier
            };
        }

        private static double QualityFromLatency(double latencyMs)
        {
            return 1.0 - Math.Min(latencyMs / 10000.0, 1.0);
        }
    }

    public sealed class ProjectionResult
    {
        public double PredictedLatencyMs { get; init; }
        public double PredictedQuality { get; init; }
        public double PredictedCost { get; init; }
        public double RiskScore { get; init; }
        public double Confidence { get; init; }
        public string RecommendationTier { get; init; } = "fair";
    }

    public Task<List<ProviderScore>> ScoreProvidersAsync(
        IReadOnlyList<string> providers,
        IReadOnlyList<string> freeModels,
        string taskType = "general",
        bool force = false,
        CancellationToken ct = default)
    {
        var freeSet = freeModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var weights = GetDynamicWeights(taskType);

        // Phase 1: Filter — skip providers with open circuit breakers
        var eligible = providers
            .Where(p => IsCircuitClosed(p))
            .ToList();

        // Phase 2: Ping — simulate concurrent network latency probes
        return ScorePhaseAsync(eligible, freeSet, weights, taskType, ct);
    }

    private async Task<List<ProviderScore>> ScorePhaseAsync(
        List<string> providers,
        HashSet<string> freeSet,
        Dictionary<string, double> weights,
        string taskType,
        CancellationToken ct)
    {
        var pingTasks = providers.Select(p => SimulatePingAsync(p, ct)).ToList();
        var pingResults = await Task.WhenAll(pingTasks);

        var results = new List<ProviderScore>(providers.Count);

        for (int i = 0; i < providers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var provider = providers[i];
            var pingMs = pingResults[i];
            var stats = _stats.GetOrAdd(provider, _ => new RouterStats());
            var isFree = freeSet.Contains(provider);

            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var latencyScore = 1.0 - Math.Min(pingMs / 5000.0, 0.95);
            scores["latency"] = latencyScore;

            var qualityScore = stats.RecentQuality;
            scores["quality"] = qualityScore;

            var costScore = isFree ? 1.0 : 0.3;
            scores["cost"] = costScore;

            var capabilityScore = ComputeCapabilityScore(provider, taskType);
            scores["capability"] = capabilityScore;

            var hoursSinceLastUse = (DateTime.UtcNow - stats.LastUsed).TotalHours;
            var freshnessScore = stats.TotalCalls == 0 ? 0.5 : Math.Max(0, 1.0 - hoursSinceLastUse / 24.0);
            scores["freshness"] = freshnessScore;

            var rateLimitScore = ComputeRateLimitScore(stats);
            scores["rate_limit"] = rateLimitScore;

            var healthFactor = ComputeHealthFactor(stats, latencyScore, qualityScore, rateLimitScore);
            scores["health_factor"] = healthFactor;

            // Fill remaining dimensions
            scores["cache"] = 0.5;
            scores["sticky"] = 0.5;
            scores["hifloat8"] = 0.5;
            scores["elo"] = 0.5;
            scores["long_term_reward"] = 0.5;
            scores["thompson"] = 0.5;
            scores["exploration"] = 0.5;
            scores["budget"] = 0.5;

            double total = 0;
            foreach (var kv in weights)
            {
                if (scores.TryGetValue(kv.Key, out var score))
                    total += kv.Value * score;
            }
            total *= healthFactor;

            results.Add(new ProviderScore
            {
                Provider = provider,
                Alive = true,
                IsFree = isFree,
                Scores = scores,
                Total = total,
                Latency = pingMs,
                AvgLatencyMs = pingMs,
                SuccessRate = stats.SuccessRate,
                CapabilityMatch = capabilityScore
            });
        }

        results.Sort((a, b) => b.Total.CompareTo(a.Total));

        _logger?.LogDebug("Holistic election completed for {TaskType}: {Count} providers scored", taskType, results.Count);
        return results;
    }

    private static async Task<double> SimulatePingAsync(string provider, CancellationToken ct)
    {
        var rng = Random.Shared;
        await Task.Delay(rng.Next(50, 201), ct);
        return rng.Next(50, 201);
    }

    private static double ComputeCapabilityScore(string provider, string taskType)
    {
        if (!ProviderCapabilities.TryGetValue(provider, out var capabilities))
            return 0.1;

        var taskLower = taskType.ToLowerInvariant();
        var matches = capabilities.Count(c => taskLower.Contains(c) || c.Contains(taskLower));

        if (matches == 0)
        {
            // Check partial substring matches
            matches = capabilities.Count(c => taskLower.Split(' ', '_', '-').Any(t => c.StartsWith(t) || t.StartsWith(c)));
        }

        return Math.Min(1.0, Math.Max(0.1, 0.5 + matches * 0.15));
    }

    private static double ComputeRateLimitScore(RouterStats stats)
    {
        if (stats.LastRateLimit == null)
            return 1.0;

        var secondsSince = (DateTime.UtcNow - stats.LastRateLimit.Value).TotalSeconds;
        if (secondsSince > 300)
            return 1.0;
        if (secondsSince < 1)
            return 0.2;

        return Math.Max(0.2, Math.Min(1.0, secondsSince / 300.0));
    }

    private static double ComputeHealthFactor(RouterStats stats, double latencyScore, double qualityScore, double rateLimitScore)
    {
        double factor = 1.0;

        if (qualityScore < 0.3)
            factor -= 0.3;
        if (latencyScore < 0.2)
            factor -= 0.2;
        if (stats.TotalCalls > 0 && stats.TotalFailures > stats.TotalCalls / 2)
            factor -= 0.2;
        if (rateLimitScore < 0.5)
            factor -= 0.25;

        return Math.Clamp(factor, 0.05, 1.0);
    }

    private bool IsCircuitClosed(string provider)
    {
        // Circuit breaker is not yet available in this assembly.
        // When a CircuitBreaker service is registered via DI, it will be injected here.
        return true;
    }

    public static Dictionary<string, double> GetDynamicWeights(string taskType, double complexity = 0.5)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["latency"] = 0.18,
            ["quality"] = 0.23,
            ["cost"] = 0.15,
            ["capability"] = 0.12,
            ["freshness"] = 0.05,
            ["rate_limit"] = 0.07,
            ["cache"] = 0.10,
            ["sticky"] = 0.10,
            ["hifloat8"] = 0.0,
            ["elo"] = 0.0,
            ["long_term_reward"] = 0.0,
            ["thompson"] = 0.0,
            ["exploration"] = 0.0,
            ["budget"] = 0.0
        };

        switch (taskType.ToLowerInvariant())
        {
            case "code":
                weights["quality"] = 0.28;
                weights["latency"] = 0.08;
                weights["capability"] = 0.16;
                break;
            case "reasoning":
                weights["quality"] = 0.28;
                weights["latency"] = 0.10;
                weights["cost"] = 0.12;
                break;
            case "chat":
                weights["latency"] = 0.20;
                weights["quality"] = 0.20;
                weights["cost"] = 0.12;
                break;
            case "search":
                weights["capability"] = 0.16;
                weights["latency"] = 0.20;
                weights["quality"] = 0.18;
                break;
            case "long_context":
                weights["hifloat8"] = 0.20;
                weights["quality"] = 0.20;
                weights["cost"] = 0.10;
                break;
        }

        // Adjust by complexity: higher complexity → favor quality more
        if (complexity > 0.7)
        {
            weights["quality"] = Math.Min(1.0, weights.GetValueOrDefault("quality") * 1.15);
            weights["latency"] = Math.Max(0.0, weights.GetValueOrDefault("latency") * 0.85);
        }

        return weights;
    }

    public RouterStats GetOrCreateStats(string provider)
    {
        return _stats.GetOrAdd(provider, _ => new RouterStats());
    }
}
