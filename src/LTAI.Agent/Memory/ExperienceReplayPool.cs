// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  ExperienceReplayPool — offline self-distillation for
//  agent trajectories.
//
//  V2: DeNovoSWE-inspired difficulty-aware filtering.
//  Integrates ShapleyEstimator for marginal contribution
//  scoring and implements difficulty-tiered sampling to
//  prioritize high-learning-potential trajectories.
//
//  Reference: arXiv:2606.10728 (DeNovoSWE)
// ═══════════════════════════════════════════════════════

using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class ExperienceReplayPool
{
    private readonly PalaceStore _store;
    private readonly ShapleyEstimator _shapley;
    private readonly ILogger<ExperienceReplayPool>? _logger;
    private readonly string _agentId;

    // ── DeNovoSWE-inspired constants ──

    /// <summary>Minimum Shapley value to consider (marginal contribution threshold).</summary>
    private const double MinLearningPotential = 1.5;

    /// <summary>Max experiences to keep in the active pool.</summary>
    private const int MaxPoolSize = 200;

    /// <summary>Difficulty tier thresholds.</summary>
    private const double HardThreshold = 0.7;
    private const double MediumThreshold = 0.4;

    /// <summary>Ratio of hard:medium:easy samples in difficulty-aware mode.</summary>
    private static readonly (double Hard, double Medium, double Easy) DifficultyMixRatio = (0.5, 0.35, 0.15);

    public ExperienceReplayPool(PalaceStore store, string agentId = "default",
        ShapleyEstimator? shapley = null, ILogger<ExperienceReplayPool>? logger = null)
    {
        _store = store;
        _shapley = shapley ?? new ShapleyEstimator(numSamples: 100);
        _agentId = agentId;
        _logger = logger;
    }

    /// <summary>
    /// Record a successful agent interaction as an experience trace.
    /// </summary>
    public async Task RecordExperienceAsync(string query, string response,
        bool success, string category, CancellationToken ct = default)
    {
        var content = $"## Experience: {category}\nQ: {query}\nA: {response}";
        await _store.StoreAsync("experience", _agentId, content,
            role: "assistant",
            importance: success ? 0.5 : 0.2,
            agentId: _agentId,
            ttlMs: 7 * 24 * 3600 * 1000) // 7 days
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sample high-value experiences using DeNovoSWE difficulty-aware filtering.
    /// Prefers hard (high-difficulty, high-Shapley-value) trajectories.
    /// </summary>
    public async Task<string> SampleAsync(int maxCount = 5, CancellationToken ct = default)
    {
        var experiences = _store.SearchByRoom("experience", _agentId, maxCount: 50);
        if (experiences.Count == 0) return "";

        // Rank by difficulty-aware score: Shapley value × importance × recency
        var scored = ScoreByDifficulty(experiences, maxCount);

        return string.Join("\n---\n", scored.Select(s => s.content));
    }

    /// <summary>
    /// DeNovoSWE-style difficulty-tiered sampling.
    /// Returns a balanced mix: 50% hard, 35% medium, 15% easy samples,
    /// each scored by Shapley marginal contribution.
    /// </summary>
    public async Task<string> SampleDifficultyAwareAsync(int maxCount = 5, CancellationToken ct = default)
    {
        var experiences = _store.SearchByRoom("experience", _agentId, maxCount: MaxPoolSize);
        if (experiences.Count == 0) return "";

        var hardCount = Math.Max(1, (int)(maxCount * DifficultyMixRatio.Hard));
        var mediumCount = Math.Max(1, (int)(maxCount * DifficultyMixRatio.Medium));
        var easyCount = Math.Max(0, maxCount - hardCount - mediumCount);

        var results = new List<(string content, double score, string tier)>();

        // Tier partitioning by difficulty (Shapley value)
        var queries = experiences.Select(e => e.Content).ToList();
        var shapleyValues = _shapley.Estimate(queries, "software engineering task resolution");

        for (int i = 0; i < Math.Min(experiences.Count, shapleyValues.Length); i++)
        {
            var e = experiences[i];
            var sv = shapleyValues[i];
            var difficulty = CalculateDifficulty(e.Content, sv);

            var score = sv * 10.0 + e.Importance * 5.0 +
                        Math.Max(0, 1.0 - (DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAt)).TotalDays / 7.0);

            var tier = difficulty >= HardThreshold ? "hard"
                : difficulty >= MediumThreshold ? "medium" : "easy";

            results.Add((e.Content, score, tier));
        }

        var sampled = new List<string>();

        // Pick top-N by score within each tier
        foreach (var tier in new[] { "hard", "medium", "easy" })
        {
            var tierCount = tier switch { "hard" => hardCount, "medium" => mediumCount, _ => easyCount };
            var tierResults = results
                .Where(r => r.tier == tier)
                .OrderByDescending(r => r.score)
                .Take(tierCount)
                .Select(r => r.content);
            sampled.AddRange(tierResults);
        }

        _logger?.LogInformation(
            "ExperienceReplayPool: difficulty-aware sampled {Total} from {Pool} (H/M/E ratio: {Hard}/{Med}/{Easy})",
            sampled.Count, experiences.Count,
            sampled.Count(r => CalculateDifficulty(r, ShapleyEstimate(r, queries)) >= HardThreshold),
            sampled.Count(r => CalculateDifficulty(r, 0.5) >= MediumThreshold),
            sampled.Count - sampled.Count(r => CalculateDifficulty(r, 0.5) >= MediumThreshold));

        return sampled.Count > 0 ? string.Join("\n---\n", sampled) : "";
    }

    /// <summary>
    /// DeNovoFilter: strictly filter by difficulty tier.
    /// Returns only experiences at or above the specified difficulty level.
    /// </summary>
    public async Task<List<string>> DeNovoFilterAsync(
        DifficultyTier minTier = DifficultyTier.Medium,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        var experiences = _store.SearchByRoom("experience", _agentId, maxCount: MaxPoolSize);
        if (experiences.Count == 0) return [];

        var queries = experiences.Select(e => e.Content).ToList();
        var shapleyValues = _shapley.Estimate(queries, "software engineering task resolution");

        var filtered = new List<(string Content, double Difficulty, double Shapley)>();
        for (int i = 0; i < Math.Min(experiences.Count, shapleyValues.Length); i++)
        {
            var difficulty = CalculateDifficulty(experiences[i].Content, shapleyValues[i]);
            var tier = difficulty >= HardThreshold ? DifficultyTier.Hard
                : difficulty >= MediumThreshold ? DifficultyTier.Medium
                : DifficultyTier.Easy;

            if (tier >= minTier)
                filtered.Add((experiences[i].Content, difficulty, shapleyValues[i]));
        }

        var result = filtered
            .OrderByDescending(f => f.Difficulty)
            .ThenByDescending(f => f.Shapley)
            .Take(maxResults)
            .Select(f => f.Content)
            .ToList();

        _logger?.LogInformation(
            "ExperienceReplayPool.DeNovoFilter: {Count}/{Total} trajectories >= {Tier}",
            result.Count, experiences.Count, minTier);

        return result;
    }

    /// <summary>
    /// Consolidate recent experiences and inject them as system-level context.
    /// Uses difficulty-aware prioritization.
    /// </summary>
    public async Task<string> ConsolidateAsync(CancellationToken ct = default)
    {
        var recent = _store.SearchByRoom("experience", _agentId, maxCount: MaxPoolSize);
        if (recent.Count == 0) return "";

        var queries = recent.Select(e => e.Content).ToList();
        var shapleyValues = _shapley.Estimate(queries, "software engineering task resolution");

        var successful = recent
            .Where(e => e.Importance >= 0.4)
            .Select((e, i) => new { Experience = e, Shapley = i < shapleyValues.Length ? shapleyValues[i] : 0.0 })
            .Select(x => new
            {
                x.Experience,
                x.Shapley,
                Difficulty = CalculateDifficulty(x.Experience.Content, x.Shapley)
            })
            .OrderByDescending(x => x.Difficulty)    // Prefer hard experiences
            .ThenByDescending(x => x.Shapley)         // Then by marginal contribution
            .ThenByDescending(x => x.Experience.Importance)
            .Take(10)
            .ToList();

        if (successful.Count == 0) return "";

        var lines = new List<string>
        {
            "## 🧠 Experience Replay (DeNovoSWE Difficulty-Aware)",
            "以下是从历史成功交互中提取的经验模式（按难度排序），可供参考：",
            "",
            $"**难度分布**: {successful.Count(x => x.Difficulty >= HardThreshold)} 高难度 / " +
            $"{successful.Count(x => x.Difficulty >= MediumThreshold && x.Difficulty < HardThreshold)} 中等 / " +
            $"{successful.Count(x => x.Difficulty < MediumThreshold)} 低难度",
            ""
        };

        int idx = 0;
        foreach (var exp in successful.Take(5))
        {
            idx++;
            var tierTag = exp.Difficulty >= HardThreshold ? "🔴 HARD"
                : exp.Difficulty >= MediumThreshold ? "🟡 MEDIUM" : "🟢 EASY";
            var snippet = exp.Experience.Content.Length > 300
                ? exp.Experience.Content[..297] + "..."
                : exp.Experience.Content;
            lines.Add($"### 经验 #{idx} [{tierTag}] (Shapley={exp.Shapley:F2})");
            lines.Add(snippet);
            lines.Add("");
        }

        _logger?.LogInformation("ExperienceReplayPool: consolidated {N} difficulty-aware experiences", successful.Count);
        return string.Join("\n", lines);
    }

    // ── Difficulty Scoring (DeNovoSWE-inspired) ──

    /// <summary>
    /// Calculate experience difficulty based on content complexity and Shapley value.
    /// Considers: code density, tool call diversity, error rate, and model confidence.
    /// </summary>
    internal static double CalculateDifficulty(string content, double shapleyValue)
    {
        if (string.IsNullOrEmpty(content)) return 0.0;

        // Code density: more code blocks → harder
        var codeBlockCount = CountOccurrences(content, "```");
        var codeDensity = Math.Min(1.0, codeBlockCount / 6.0);

        // Tool interaction complexity: more tool-like keywords → harder
        var toolKeywords = new[] { "RunAsync", "ExecuteTool", "FunctionCall", "ToolResult",
            "CallId", "Arguments", "Result:", "tool_name" };
        var toolCount = toolKeywords.Sum(k => CountOccurrences(content, k));
        var toolComplexity = Math.Min(1.0, toolCount / 10.0);

        // Error/retry density: more errors → harder to learn from
        var errorCount = CountOccurrences(content, "error") + CountOccurrences(content, "Error")
            + CountOccurrences(content, "exception") + CountOccurrences(content, "fail");
        var errorDensity = Math.Min(1.0, errorCount / 8.0);

        // Length bonus: very short or very long experiences are harder
        var lengthFactor = content.Length < 200
            ? 0.3
            : content.Length > 5000
                ? 0.7
                : 0.5 + (content.Length - 200) / 9600.0; // 0.5-1.0 for 200-5000 chars

        // Shapley value contributes to difficulty (higher Shapley = more unique = harder)
        var shapleyFactor = Math.Min(1.0, shapleyValue * 2.0);

        // Weighted combination
        return 0.25 * codeDensity
             + 0.20 * toolComplexity
             + 0.20 * errorDensity
             + 0.15 * lengthFactor
             + 0.20 * shapleyFactor;
    }

    // ── Scoring Utilities ──

    private List<(string content, double score)> ScoreByDifficulty(
        IReadOnlyList<PalaceStore.Drawer> experiences, int maxCount)
    {
        var queries = experiences.Select(e => e.Content).ToList();
        var shapleyValues = _shapley.Estimate(queries, "software engineering task resolution");

        return experiences
            .Select((e, i) =>
            {
                var sv = i < shapleyValues.Length ? shapleyValues[i] : 0.0;
                var difficulty = CalculateDifficulty(e.Content, sv);

                // DeNovoSWE score: prioritize high difficulty × high importance × recency
                var recency = Math.Max(0, 1.0 -
                    (DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAt)).TotalDays / 7.0);
                var score = difficulty * 20.0 + e.Importance * 10.0 + sv * 5.0 + recency;

                return (content: e.Content, score);
            })
            .OrderByDescending(x => x.score)
            .Take(maxCount)
            .ToList();
    }

    private static double ShapleyEstimate(string content, List<string> queries)
    {
        // Quick inline estimate for logging — avoid full recomputation
        var codeBlockCount = CountOccurrences(content, "```");
        var toolCount = CountOccurrences(content, "tool") + CountOccurrences(content, "Tool");
        return Math.Min(1.0, (codeBlockCount * 0.1 + toolCount * 0.05));
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}

/// <summary>Difficulty tier for DeNovoSWE-style filtering.</summary>
public enum DifficultyTier
{
    Easy = 0,
    Medium = 1,
    Hard = 2
}
