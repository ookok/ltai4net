using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record MetaCognitiveAssessment
{
    public float Certainty { get; init; }
    public bool ShouldDelegate { get; init; }
    public string DelegationReason { get; init; } = "";
    public float SelfModelAccuracy { get; init; }
    public float Familiarity { get; init; }
    public float Novelty { get; init; }
    public string Assessment { get; init; } = "";
}

public sealed record PathReliabilityAssessment
{
    public double Reliability { get; init; }
    public bool ShouldFallbackToLLM { get; init; }
    public string Reason { get; init; } = "";
    public double KgCoverage { get; init; }
    public double HistoricalSuccessRate { get; init; }
    public int DepthPenalty { get; init; }
}

public sealed class MetaCognitiveLayer
{
    private readonly ILogger<MetaCognitiveLayer> _logger;
    private readonly Dictionary<string, float> _domainFamiliarity = new();
    private readonly Dictionary<string, int> _domainQueryCount = new();
    private readonly Dictionary<string, int> _domainSuccessCount = new();
    private readonly Dictionary<string, PathStats> _pathStats = new();
    private int _totalQueries;
    private int _totalDelegations;
    private readonly object _lock = new();

    public MetaCognitiveLayer(ILogger<MetaCognitiveLayer>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MetaCognitiveLayer>.Instance;
    }

    public MetaCognitiveAssessment Assess(string query, float localConfidence, string? domain = null)
    {
        lock (_lock) { _totalQueries++; }

        var d = domain ?? InferDomain(query);
        var familiarity = GetDomainFamiliarity(d);
        var novelty = ComputeNovelty(query);
        var certainty = localConfidence * familiarity * GetRecentSuccessRate(d);

        var shouldDelegate = certainty < 0.4f ||
                             novelty > 0.7f ||
                             IsUnfamiliarDomain(d);

        var reason = shouldDelegate
            ? GetDelegationReason(certainty, novelty, familiarity)
            : "";

        if (shouldDelegate)
            lock (_lock) { _totalDelegations++; }

        return new MetaCognitiveAssessment
        {
            Certainty = certainty,
            ShouldDelegate = shouldDelegate,
            DelegationReason = reason,
            Familiarity = familiarity,
            Novelty = novelty,
            SelfModelAccuracy = ComputeSelfModelAccuracy(),
            Assessment = BuildAssessmentText(certainty, familiarity, novelty, shouldDelegate)
        };
    }

    private float GetRecentSuccessRate(string domain)
    {
        if (!_domainQueryCount.TryGetValue(domain, out var total) || total < 3)
            return 0.8f;

        var success = _domainSuccessCount.GetValueOrDefault(domain, 0);
        return (float)success / total;
    }

    public void RecordOutcome(string query, bool success, string? domain = null)
    {
        var d = domain ?? InferDomain(query);
        lock (_lock)
        {
            _domainQueryCount[d] = _domainQueryCount.GetValueOrDefault(d) + 1;
            if (success)
                _domainSuccessCount[d] = _domainSuccessCount.GetValueOrDefault(d) + 1;
            _domainFamiliarity[d] = ComputeDomainSuccessRate(d);
        }
    }

    public void ReinforceDomain(string domain, float delta)
    {
        lock (_lock)
        {
            _domainFamiliarity[domain] = Math.Clamp(_domainFamiliarity.GetValueOrDefault(domain, 0.1f) + delta, 0.0f, 1.0f);
        }
    }

    public Dictionary<string, object> GetMetrics()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_queries"] = _totalQueries,
                ["total_delegations"] = _totalDelegations,
                ["delegation_rate"] = _totalQueries > 0 ? (float)_totalDelegations / _totalQueries : 0f,
                ["domain_count"] = _domainFamiliarity.Count,
                ["avg_familiarity"] = _domainFamiliarity.Count > 0 ? _domainFamiliarity.Values.Average() : 0f
            };
        }
    }

    private float GetDomainFamiliarity(string domain)
    {
        return _domainFamiliarity.GetValueOrDefault(domain, 0.1f);
    }

    private bool IsUnfamiliarDomain(string domain)
    {
        return GetDomainFamiliarity(domain) < 0.3f;
    }

    private float ComputeDomainSuccessRate(string domain)
    {
        if (!_domainQueryCount.TryGetValue(domain, out var total) || total == 0)
            return 0.1f;

        var success = _domainSuccessCount.GetValueOrDefault(domain, 0);
        return (float)success / total;
    }

    private static float ComputeNovelty(string query)
    {
        var commonPrefixes = new[] { "what", "how", "why", "when", "where", "who", "which", "什么", "怎么", "为什么", "如何" };
        var lower = query.ToLowerInvariant();

        var isCommonPattern = commonPrefixes.Any(p => lower.StartsWith(p));
        var lengthFactor = Math.Clamp(query.Length / 200f, 0f, 1f);
        var patternBonus = isCommonPattern ? 0.1f : 0.3f;

        return Math.Clamp(lengthFactor * 0.5f + patternBonus, 0f, 1f);
    }

    private float ComputeSelfModelAccuracy()
    {
        if (_domainFamiliarity.Count == 0) return 0.1f;
        return (float)_domainFamiliarity.Values.Average();
    }

    private static string GetDelegationReason(float certainty, float novelty, float familiarity)
    {
        if (certainty < 0.2f) return "very low certainty";
        if (novelty > 0.8f) return "highly novel query";
        if (familiarity < 0.2f) return "unfamiliar domain";
        if (certainty < 0.4f) return "low certainty";
        return "moderate uncertainty";
    }

    private static string BuildAssessmentText(float certainty, float familiarity, float novelty, bool delegate_)
    {
        if (delegate_)
            return $"Uncertain (certainty={certainty:F2}, familiarity={familiarity:F2}, novelty={novelty:F2})";
        return $"Confident (certainty={certainty:F2}, familiarity={familiarity:F2})";
    }

    private static string InferDomain(string query)
    {
        return DomainKeywords.InferDomain(query);
    }

    public PathReliabilityAssessment AssessPathReliability(
        List<string> path,
        string? domain = null,
        int maxDepth = 5)
    {
        if (path.Count == 0)
        {
            return new PathReliabilityAssessment
            {
                Reliability = 0,
                ShouldFallbackToLLM = true,
                Reason = "empty path"
            };
        }

        var d = domain ?? "general";
        var kgCoverage = ComputeKGCoverage(path, d);
        var historicalSuccess = GetPathHistoricalSuccess(path, d);
        var depthPenalty = Math.Max(0, path.Count - maxDepth);
        var depthFactor = depthPenalty > 0 ? 1.0 / (1.0 + depthPenalty * 0.2) : 1.0;

        var reliability = 0.40 * kgCoverage + 0.40 * historicalSuccess + 0.20 * depthFactor;

        var shouldFallback = reliability < 0.4 || depthPenalty > maxDepth || !HasAnyPathHistory(path, d);

        var reason = shouldFallback
            ? reliability < 0.4 ? $"low reliability ({reliability:F2})"
            : depthPenalty > maxDepth ? $"too deep ({path.Count} > {maxDepth})"
            : "no prior success with this path"
            : "";

        return new PathReliabilityAssessment
        {
            Reliability = Math.Clamp(reliability, 0, 1),
            ShouldFallbackToLLM = shouldFallback,
            Reason = reason,
            KgCoverage = kgCoverage,
            HistoricalSuccessRate = historicalSuccess,
            DepthPenalty = depthPenalty
        };
    }

    public void RecordPathOutcome(List<string> path, bool success, string? domain = null)
    {
        var key = PathKey(path, domain);
        lock (_lock)
        {
            if (!_pathStats.TryGetValue(key, out var stats))
            {
                stats = new PathStats();
                _pathStats[key] = stats;
            }
            stats.Attempts++;
            if (success)
                stats.Successes++;
            stats.LastUsed = DateTime.UtcNow;
        }
    }

    private double ComputeKGCoverage(List<string> path, string domain)
    {
        if (_pathStats.TryGetValue(PathKey(path, domain), out var stats) && stats.KgNodesMatched > 0)
            return Math.Min(1.0, (double)stats.KgNodesMatched / path.Count);

        return 0.1;
    }

    private double GetPathHistoricalSuccess(List<string> path, string domain)
    {
        var key = PathKey(path, domain);
        if (!_pathStats.TryGetValue(key, out var stats) || stats.Attempts < 2)
            return 0.2;

        return (double)stats.Successes / stats.Attempts;
    }

    private bool HasAnyPathHistory(List<string> path, string domain)
    {
        return _pathStats.TryGetValue(PathKey(path, domain), out var stats) && stats.Attempts > 0;
    }

    public void RecordKGCoverage(List<string> path, int nodesMatched, string? domain = null)
    {
        var key = PathKey(path, domain);
        lock (_lock)
        {
            if (!_pathStats.TryGetValue(key, out var stats))
            {
                stats = new PathStats();
                _pathStats[key] = stats;
            }
            stats.KgNodesMatched = Math.Max(stats.KgNodesMatched, nodesMatched);
        }
    }

    private static string PathKey(List<string> path, string? domain)
    {
        var d = domain ?? "general";
        return $"{d}:{string.Join(">", path.Select(p => p.Length > 20 ? p[..20] : p))}";
    }

    private sealed class PathStats
    {
        public int Attempts;
        public int Successes;
        public int KgNodesMatched;
        public DateTime LastUsed;
    }
}
