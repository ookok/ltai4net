using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record KnowledgeGap
{
    public string Topic { get; init; } = "";
    public string Domain { get; init; } = "";
    public float Severity { get; init; }
    public int FailedAttempts { get; init; }
    public DateTime FirstDetected { get; init; }
    public DateTime LastDetected { get; init; }
    public List<string> RelatedQueries { get; init; } = new();
    public string SuggestedAction { get; init; } = "";
}

public sealed class KnowledgeGapDetector
{
    private readonly Dictionary<string, KnowledgeGap> _gaps = new();
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly ILogger<KnowledgeGapDetector> _logger;
    private readonly object _lock = new();

    public KnowledgeGapDetector(
        MetaCognitiveLayer metaCognition,
        ILogger<KnowledgeGapDetector>? logger = null)
    {
        _metaCognition = metaCognition;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeGapDetector>.Instance;
    }

    public void RecordFailedQuery(string query, string domain, string reason)
    {
        var topic = ExtractTopic(query);
        var gapKey = $"{domain}:{topic}";

        lock (_lock)
        {
            if (!_gaps.TryGetValue(gapKey, out var gap))
            {
                gap = new KnowledgeGap
                {
                    Topic = topic,
                    Domain = domain,
                    Severity = 0.1f,
                    FailedAttempts = 1,
                    FirstDetected = DateTime.UtcNow,
                    LastDetected = DateTime.UtcNow,
                    RelatedQueries = new() { query },
                    SuggestedAction = GenerateSuggestedAction(domain, topic)
                };
                _gaps[gapKey] = gap;
            }
            else
            {
                var relatedQueries = new List<string>(gap.RelatedQueries) { query };
                if (relatedQueries.Count > 10)
                    relatedQueries = relatedQueries.TakeLast(10).ToList();

                _gaps[gapKey] = gap with
                {
                    FailedAttempts = gap.FailedAttempts + 1,
                    LastDetected = DateTime.UtcNow,
                    Severity = Math.Min(gap.Severity + 0.1f, 1.0f),
                    RelatedQueries = relatedQueries
                };
            }

            if (_gaps[gapKey].FailedAttempts >= 3)
            {
                _logger.LogWarning("Knowledge gap detected: domain={Domain}, topic={Topic}, severity={Severity:F2}, failures={Failures}",
                    domain, topic, _gaps[gapKey].Severity, _gaps[gapKey].FailedAttempts);
            }
        }
    }

    public List<KnowledgeGap> GetActiveGaps(float minSeverity = 0.3f)
    {
        lock (_lock)
        {
            return _gaps.Values
                .Where(g => g.Severity >= minSeverity)
                .OrderByDescending(g => g.Severity)
                .ToList();
        }
    }

    public List<string> GetLearningSuggestions(int maxSuggestions = 5)
    {
        var gaps = GetActiveGaps(0.5f);
        return gaps.Take(maxSuggestions)
            .Select(g => $"[{g.Domain}] {g.Topic}: {g.SuggestedAction}")
            .ToList();
    }

    public void ResolveGap(string domain, string topic)
    {
        var gapKey = $"{domain}:{topic}";
        lock (_lock)
        {
            if (_gaps.Remove(gapKey))
            {
                _logger.LogInformation("Knowledge gap resolved: domain={Domain}, topic={Topic}", domain, topic);
            }
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var byDomain = _gaps.Values.GroupBy(g => g.Domain)
                .ToDictionary(g => g.Key, g => new { Count = g.Count(), AvgSeverity = g.Average(x => x.Severity) });

            return new Dictionary<string, object>
            {
                ["total_gaps"] = _gaps.Count,
                ["high_severity_gaps"] = _gaps.Values.Count(g => g.Severity >= 0.7f),
                ["by_domain"] = byDomain,
                ["avg_severity"] = _gaps.Count > 0 ? _gaps.Values.Average(g => g.Severity) : 0f
            };
        }
    }

    private static string ExtractTopic(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2)
            .Take(3);
        return string.Join(" ", words);
    }

    private static string GenerateSuggestedAction(string domain, string topic)
    {
        return domain switch
        {
            "code" => $"Search documentation or examples for: {topic}",
            "math" => $"Review mathematical concepts related to: {topic}",
            "science" => $"Study scientific literature on: {topic}",
            "language" => $"Learn grammar/vocabulary for: {topic}",
            "system" => $"Investigate system configuration for: {topic}",
            _ => $"Research and learn about: {topic}"
        };
    }
}
