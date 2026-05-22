using LTAI.Knowledge.Core;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prompting;

public sealed record LearningFeedback
{
    public string Query { get; init; } = "";
    public string GeneratedAnswer { get; init; } = "";
    public string? RoleUsed { get; init; }
    public HallucinationVerdict HallucinationVerdict { get; init; } = new(true, 1.0, "ok", new());
    public double QualityScore { get; init; }
    public List<string> ExtractedPatterns { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LearningLoopResult
{
    public string Answer { get; init; } = "";
    public HallucinationVerdict HallucinationCheck { get; init; } = new(true, 1.0, "ok", new());
    public string OptimizedRole { get; init; } = "";
    public List<AutoMinedTerm> MinedTerms { get; init; } = new();
    public List<ExtractedTemplate> ExtractedTemplates { get; init; } = new();
    public Dictionary<string, object> QualityMetrics { get; init; } = new();
    public bool Improved { get; init; }
    public int IterationCount { get; init; }
}

public sealed class ContinuousLearningLoop
{
    private readonly List<LearningFeedback> _feedbackHistory = new();
    private readonly Dictionary<string, double> _rolePerformance = new();
    private readonly Dictionary<string, int> _roleUsage = new();
    private const int MaxHistory = 200;
    private readonly object _lock = new();
    private readonly ILogger<ContinuousLearningLoop>? _logger;

    public ContinuousLearningLoop(ILogger<ContinuousLearningLoop>? logger = null)
    {
        _logger = logger;
    }

    public LearningLoopResult Process(
        string query,
        string generatedAnswer,
        string? roleUsed = null)
    {
        var hallucinationCheck = HallucinationGuard.Instance.CheckGeneration(generatedAnswer);

        var optimizedRole = roleUsed ?? "general";
        if (!hallucinationCheck.Passed && !string.IsNullOrEmpty(roleUsed))
        {
            optimizedRole = OptimizeRoleFromFeedback(query, roleUsed, hallucinationCheck.Score);
        }

        var templates = LearningEngine.Instance.ExtractTemplate(generatedAnswer, optimizedRole);
        var minedTerms = LearningEngine.Instance.MineTerms(generatedAnswer, $"generation_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

        var feedback = new LearningFeedback
        {
            Query = query,
            GeneratedAnswer = generatedAnswer,
            RoleUsed = optimizedRole,
            HallucinationVerdict = hallucinationCheck,
            QualityScore = ComputeQualityScore(hallucinationCheck, generatedAnswer),
            ExtractedPatterns = minedTerms.Select(t => t.Term).ToList()
        };

        RecordFeedback(feedback);

        var metrics = GetQualityMetrics();

        _logger?.LogInformation(
            "LearningLoop: hallucination={HallucScore:F2} role={Role} quality={Quality:F2} terms={TermCount}",
            hallucinationCheck.Score, optimizedRole, feedback.QualityScore, minedTerms.Count);

        return new LearningLoopResult
        {
            Answer = generatedAnswer,
            HallucinationCheck = hallucinationCheck,
            OptimizedRole = optimizedRole,
            MinedTerms = minedTerms,
            ExtractedTemplates = templates != null ? new() { templates } : new(),
            QualityMetrics = metrics,
            Improved = feedback.QualityScore >= GetAverageQuality(),
            IterationCount = _feedbackHistory.Count
        };
    }

    public string OptimizeRoleFromFeedback(string query, string currentRole, double hallucinationScore)
    {
        lock (_lock)
        {
            if (_roleUsage.TryGetValue(currentRole, out var count))
            {
                var perf = _rolePerformance.GetValueOrDefault(currentRole, 0.5);
                var updatedPerf = perf * 0.8 + (1.0 - hallucinationScore) * 0.2;
                _rolePerformance[currentRole] = updatedPerf;
                _roleUsage[currentRole] = count + 1;
            }
            else
            {
                _rolePerformance[currentRole] = 1.0 - hallucinationScore;
                _roleUsage[currentRole] = 1;
            }
        }

        if (hallucinationScore >= 0.8)
            return currentRole;

        var alternatives = new Dictionary<string, List<string>>
        {
            ["eia_engineer"] = new() { "doc_writer", "data_analyst" },
            ["code_reviewer"] = new() { "security_auditor", "data_analyst" },
            ["data_analyst"] = new() { "code_reviewer", "doc_writer" },
            ["translator"] = new() { "doc_writer" },
            ["security_auditor"] = new() { "code_reviewer" },
            ["doc_writer"] = new() { "code_reviewer", "data_analyst" }
        };

        if (alternatives.TryGetValue(currentRole, out var alts))
        {
            lock (_lock)
            {
                var bestAlt = alts
                    .Where(a => !_rolePerformance.ContainsKey(a) || _rolePerformance[a] > 0.3)
                    .OrderByDescending(a => _rolePerformance.GetValueOrDefault(a, 0.5))
                    .FirstOrDefault();

                if (bestAlt != null) return bestAlt;
            }
        }

        return currentRole;
    }

    public void RecordFeedback(LearningFeedback feedback)
    {
        lock (_lock)
        {
            _feedbackHistory.Add(feedback);
            if (_feedbackHistory.Count > MaxHistory)
                _feedbackHistory.RemoveAt(0);
        }
    }

    public void RecordUserRating(string query, string role, double rating)
    {
        lock (_lock)
        {
            if (_rolePerformance.TryGetValue(role, out var current))
                _rolePerformance[role] = current * 0.7 + rating * 0.3;
            else
                _rolePerformance[role] = rating;

            _roleUsage[role] = _roleUsage.GetValueOrDefault(role) + 1;
        }

        LearningEngine.Instance.RecordFeedback(role, rating >= 0.5);
    }

    public Dictionary<string, object> GetQualityMetrics()
    {
        lock (_lock)
        {
            var recent = _feedbackHistory.TakeLast(50).ToList();

            var avgHallucination = recent.Count > 0
                ? recent.Average(f => 1.0 - f.HallucinationVerdict.Score)
                : 0;

            var avgQuality = recent.Count > 0
                ? recent.Average(f => f.QualityScore)
                : 0.5;

            var improvement = _feedbackHistory.Count >= 20
                ? GetAverageQuality(10) - GetAverageQuality(-10, 0)
                : 0;

            return new()
            {
                ["avg_hallucination_rate"] = Math.Round(avgHallucination, 3),
                ["avg_quality"] = Math.Round(avgQuality, 3),
                ["improvement_trend"] = Math.Round(improvement, 3),
                ["feedback_count"] = _feedbackHistory.Count,
                ["role_performance"] = (object)_rolePerformance
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .ToDictionary(k => k.Key, v => Math.Round(v.Value, 3)),
                ["hallucination_guard_status"] = HallucinationGuard.Instance.GetDashboard()
            };
        }
    }

    public double GetAverageQuality(int windowSize = 50, int offset = 0)
    {
        lock (_lock)
        {
            if (_feedbackHistory.Count == 0) return 0.5;

            var window = _feedbackHistory
                .Skip(Math.Max(0, _feedbackHistory.Count + offset - windowSize))
                .Take(windowSize)
                .ToList();

            return window.Count > 0
                ? window.Average(f => f.QualityScore)
                : 0.5;
        }
    }

    public List<LearningFeedback> GetRecentFeedback(int count = 10)
    {
        lock (_lock)
        {
            return _feedbackHistory.TakeLast(count).Reverse().ToList();
        }
    }

    private static double ComputeQualityScore(
        HallucinationVerdict hallucinationCheck,
        string answer)
    {
        double score = hallucinationCheck.Passed ? 0.7 : 0.3;
        score += hallucinationCheck.Score * 0.2;

        var hasStructure = answer.Contains("##") || answer.Contains("###") || answer.Contains("**");
        if (hasStructure) score += 0.05;

        var estimatedTokens = answer.Length / 4;
        if (estimatedTokens > 50 && estimatedTokens < 2000)
            score += 0.05;
        else if (estimatedTokens >= 2000)
            score += 0.03;

        return Math.Min(1.0, score);
    }
}
