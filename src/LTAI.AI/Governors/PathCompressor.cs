using LTAI.Core.Messaging;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record PathPattern
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public List<string> Steps { get; init; } = [];
    public string Domain { get; init; } = "";
    public int Support { get; init; }
    public double AvgReward { get; init; }
    public double AvgConfidence { get; init; }
    public double AvgLatencyMs { get; init; }
    public List<string> SourceEpisodeIds { get; init; } = [];
    public double ReliabilityScore { get; init; }
}

public sealed class PathCompressor
{
    private readonly DualMemoryStore _memory;
    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly IncrementalRuleExtractor _ruleExtractor;
    private readonly ILogger<PathCompressor>? _logger;

    public PathCompressor(
        DualMemoryStore memory,
        KnowledgeGraphBridge graphBridge,
        IncrementalRuleExtractor ruleExtractor,
        ILogger<PathCompressor>? logger = null)
    {
        _memory = memory;
        _graphBridge = graphBridge;
        _ruleExtractor = ruleExtractor;
        _logger = logger;
    }

    public async Task<List<PathPattern>> CompressActionPathsAsync(
        string? domain = null,
        int minSupport = 3,
        int maxPathLength = 5,
        CancellationToken ct = default)
    {
        var episodes = domain != null
            ? _memory.GetEpisodesByDomain(domain, limit: 500)
            : _memory.GetUnconsolidatedEpisodes(limit: 500);

        if (episodes.Count < minSupport)
        {
            _logger?.LogDebug("PathCompressor: only {Count} episodes, need >= {Min} for mining", episodes.Count, minSupport);
            return [];
        }

        var sequences = new List<(RawEpisode Episode, List<string> Steps)>();
        foreach (var ep in episodes)
        {
            var steps = ParseActionSteps(ep.FullTrajectory);
            if (steps.Count >= 2)
                sequences.Add((ep, steps));
        }

        if (sequences.Count < minSupport)
            return [];

        var patterns = MineFrequentPatterns(sequences, minSupport, maxPathLength);

        var results = new List<PathPattern>();
        foreach (var pattern in patterns)
        {
            var supportingEps = sequences
                .Where(s => ContainsSubsequence(s.Steps, pattern))
                .ToList();

            var avgReward = supportingEps.Average(s => (double)s.Episode.Reward);
            var avgConfidence = supportingEps.Average(s => (double)s.Episode.Confidence);
            var avgLatency = supportingEps.Average(s => (double)(s.Episode.Timestamp - episodes.Min(e => e.Timestamp)).TotalMilliseconds);
            var reliability = ComputeReliability(avgReward, avgConfidence, supportingEps.Count);

            results.Add(new PathPattern
            {
                Steps = pattern,
                Domain = domain ?? "general",
                Support = supportingEps.Count,
                AvgReward = avgReward,
                AvgConfidence = avgConfidence,
                AvgLatencyMs = avgLatency,
                SourceEpisodeIds = supportingEps.Select(s => s.Episode.Id.ToString()).ToList(),
                ReliabilityScore = reliability
            });
        }

        await IngestIntoKnowledgeGraph(results, ct).ConfigureAwait(false);
        await IngestAsLessons(results, ct).ConfigureAwait(false);

        _logger?.LogInformation("PathCompressor: mined {Count} path patterns from {Episodes} episodes (domain={Domain})",
            results.Count, episodes.Count, domain ?? "all");

        return results;
    }

    private static List<string> ParseActionSteps(string fullTrajectory)
    {
        var steps = new List<string>();
        if (string.IsNullOrWhiteSpace(fullTrajectory))
            return steps;

        var lines = fullTrajectory.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[TOOL]") || trimmed.StartsWith("[ACTION]") ||
                trimmed.StartsWith(">>") || trimmed.Contains("Calling:") ||
                trimmed.Contains("invoked:"))
            {
                var step = ExtractStepLabel(trimmed);
                if (!string.IsNullOrWhiteSpace(step))
                    steps.Add(step);
            }
            else if (trimmed.StartsWith("[LABEL]") || trimmed.Contains("Intent:") ||
                     trimmed.Contains("classified as:"))
            {
                var label = ExtractLabelName(trimmed);
                if (!string.IsNullOrWhiteSpace(label) && label.Length < 30)
                    steps.Add($"INTENT:{label}");
            }
        }

        return steps.Count > 0 ? steps : [fullTrajectory.Length > 100 ? fullTrajectory[..100] : fullTrajectory];
    }

    private static string ExtractStepLabel(string line)
    {
        var cleaned = line
            .Replace("[TOOL]", "").Replace("[ACTION]", "")
            .Replace(">>", "").Replace("Calling:", "")
            .Replace("invoked:", "").Trim();

        var colonIdx = cleaned.IndexOf(':');
        if (colonIdx > 0 && colonIdx < 50)
            cleaned = cleaned[..colonIdx].Trim();

        if (cleaned.Length > 40)
            cleaned = cleaned[..40];

        return cleaned;
    }

    private static string ExtractLabelName(string line)
    {
        var cleaned = line
            .Replace("[LABEL]", "").Replace("Intent:", "")
            .Replace("classified as:", "")
            .Replace("classification:", "").Trim().Trim('"', '\'');

        if (cleaned.Length > 30)
            cleaned = cleaned[..30];

        return cleaned;
    }

    private static List<List<string>> MineFrequentPatterns(
        List<(RawEpisode Episode, List<string> Steps)> sequences,
        int minSupport,
        int maxLength)
    {
        var patterns = new Dictionary<string, List<string>>();

        for (int len = 2; len <= maxLength; len++)
        {
            var candidates = new Dictionary<string, int>();
            foreach (var (_, steps) in sequences)
            {
                for (int i = 0; i <= steps.Count - len; i++)
                {
                    var subseq = steps.Skip(i).Take(len).ToList();
                    var key = string.Join("→", subseq);

                    if (!candidates.TryAdd(key, 1))
                        candidates[key]++;
                }
            }

            foreach (var (key, count) in candidates)
            {
                if (count >= minSupport && !patterns.ContainsKey(key))
                {
                    patterns[key] = key.Split("→").ToList();
                }
            }
        }

        var maxPatterns = patterns.Values
            .Where(p => !patterns.Values.Any(other => other.Count > p.Count && ContainsSubsequence(other, p)))
            .ToList();

        return maxPatterns.Count > 0 ? maxPatterns : patterns.Values.Take(20).ToList();
    }

    private static bool ContainsSubsequence(List<string> parent, List<string> child)
    {
        if (child.Count == 0 || child.Count > parent.Count)
            return false;

        int childIdx = 0;
        for (int i = 0; i < parent.Count && childIdx < child.Count; i++)
        {
            if (string.Equals(parent[i], child[childIdx], StringComparison.OrdinalIgnoreCase))
                childIdx++;
        }
        return childIdx == child.Count;
    }

    private static double ComputeReliability(double avgReward, double avgConfidence, int support)
    {
        var rewardNorm = Math.Clamp(avgReward / 5.0, 0, 1);
        var confidenceNorm = Math.Clamp(avgConfidence, 0, 1);
        var supportNorm = Math.Min(1.0, support / 20.0);

        return 0.35 * rewardNorm + 0.45 * confidenceNorm + 0.20 * supportNorm;
    }

    private async Task IngestIntoKnowledgeGraph(List<PathPattern> patterns, CancellationToken ct)
    {
        var reliablePatterns = patterns.Where(p => p.ReliabilityScore >= 0.5).ToList();

        foreach (var pattern in reliablePatterns)
        {
            try
            {
                var summary = $"Path: {string.Join(" → ", pattern.Steps)} | reliability={pattern.ReliabilityScore:F2} | support={pattern.Support}";
                _graphBridge.IngestTeachingResult(
                    $"{pattern.Domain}_path:{pattern.Id}",
                    new L2TeachingResult
                    {
                        ReasoningSteps = string.Join(" -> ", pattern.Steps),
                        KeyConcepts = $"{pattern.Domain}:reliability={pattern.ReliabilityScore:F2}"
                    });

                if (pattern.Steps.Count >= 3)
                {
                    for (int i = 0; i < pattern.Steps.Count - 1; i++)
                    {
                        _graphBridge.IngestExperience(
                            pattern.Steps[i],
                            pattern.Steps[i + 1],
                            $"path:{pattern.Domain}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PathCompressor: failed to ingest pattern {Id} into KG", pattern.Id);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task IngestAsLessons(List<PathPattern> patterns, CancellationToken ct)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                var title = $"Path:{pattern.Domain}:{string.Join(",", pattern.Steps.Take(3))}";
                var content = $"Multi-step action pattern (reliability={pattern.ReliabilityScore:F2}, support={pattern.Support}): {string.Join(" → ", pattern.Steps)}";

                _memory.StoreLesson(new AbstractLesson
                {
                    Id = ObjectId.NewObjectId(),
                    Title = title.Length > 100 ? title[..100] : title,
                    Kind = LessonKind.Strategy,
                    Content = content,
                    Domain = pattern.Domain,
                    SourceEpisodeIds = pattern.SourceEpisodeIds,
                    HelpfulCount = Math.Max(0, pattern.Support - 1),
                    HarmfulCount = Math.Max(0, (int)(pattern.Support * (1 - pattern.AvgConfidence))),
                    QualityScore = (float)pattern.ReliabilityScore,
                    CreatedAt = DateTime.UtcNow,
                    Version = 1
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PathCompressor: failed to store pattern {Id} as lesson", pattern.Id);
            }
        }

        ct.ThrowIfCancellationRequested();
    }
}
