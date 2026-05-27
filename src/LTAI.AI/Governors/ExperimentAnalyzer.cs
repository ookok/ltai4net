using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// ASI-Evolve inspired: ExperimentTrace + ExperimentAnalyzer
// Closed-loop research: trace → analyze → distilled lesson → future retrieval
// Bridges: ResponsePostProcessor, DreamCycle, HarnessEvolution → unified Analyzer
// ============================================================================

public sealed record ExperimentTrace
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Query { get; init; } = "";
    public string Hypothesis { get; init; } = "";
    public string Response { get; init; } = "";
    public string Route { get; init; } = "unknown";
    public float Complexity { get; init; }
    public float Confidence { get; init; }
    public float Reward { get; init; }
    public double LatencyMs { get; init; }
    public int ToolCallCount { get; init; }
    public List<string> ToolSequence { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public bool Success { get; init; }
    public string ModelType { get; init; } = "";
    public string Domain { get; init; } = "general";
    public Dictionary<string, double> Metrics { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed record AnalyzedLesson
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Summary { get; init; } = "";
    public string Insight { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Domain { get; init; } = "general";
    public string TriggerPattern { get; init; } = "";
    public float Impact { get; init; }
    public float Generalizability { get; init; }
    public List<string> EvidenceIds { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int ReuseCount { get; set; }
}

public sealed class ExperimentAnalyzer
{
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly HarnessEvolution? _harnessEvo;
    private readonly SynapticMemory? _synapticMemory;
    private readonly WeightSubspaceAnalyzer? _subspaceAnalyzer;
    private readonly ILogger<ExperimentAnalyzer> _logger;
    private readonly ConcurrentDictionary<string, AnalyzedLesson> _lessons = new();
    private readonly ConcurrentQueue<ExperimentTrace> _recentTraces = new();
    private readonly Dictionary<string, int> _domainSuccessCounts = new();
    private readonly Dictionary<string, int> _domainFailureCounts = new();
    private int _totalExperiments;
    private int _totalLessons;
    private const int MaxRecentTraces = 200;

    public ExperimentAnalyzer(
        ICrossRunEvolutionStore? evolutionStore = null,
        HarnessEvolution? harnessEvo = null,
        SynapticMemory? synapticMemory = null,
        WeightSubspaceAnalyzer? subspaceAnalyzer = null,
        ILogger<ExperimentAnalyzer>? logger = null)
    {
        _evolutionStore = evolutionStore;
        _harnessEvo = harnessEvo;
        _synapticMemory = synapticMemory;
        _subspaceAnalyzer = subspaceAnalyzer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ExperimentAnalyzer>.Instance;
    }

    // ========================================================================
    // 1. Record Experiment Trace
    // ========================================================================

    public ExperimentTrace RecordTrace(
        string query, string response, string route, float complexity, float confidence,
        double latencyMs, int toolCallCount, List<string> toolSequence,
        List<string>? errors = null, float reward = 0, string modelType = "",
        string domain = "general", Dictionary<string, double>? metrics = null)
    {
        var success = confidence >= 0.6f && reward >= 0.5f && (errors?.Count ?? 0) == 0;

        var trace = new ExperimentTrace
        {
            Query = query,
            Response = response,
            Route = route,
            Complexity = complexity,
            Confidence = confidence,
            Reward = reward,
            LatencyMs = latencyMs,
            ToolCallCount = toolCallCount,
            ToolSequence = toolSequence,
            Errors = errors ?? new(),
            Success = success,
            ModelType = modelType,
            Domain = domain,
            Metrics = metrics ?? new()
        };

        _recentTraces.Enqueue(trace);
        while (_recentTraces.Count > MaxRecentTraces)
            _recentTraces.TryDequeue(out _);

        lock (_domainSuccessCounts)
        {
            if (success)
                _domainSuccessCounts[domain] = _domainSuccessCounts.GetValueOrDefault(domain) + 1;
            else
                _domainFailureCounts[domain] = _domainFailureCounts.GetValueOrDefault(domain) + 1;
        }

        Interlocked.Increment(ref _totalExperiments);

        return trace;
    }

    // ========================================================================
    // 2. Analyze Trace → Distilled Lesson
    // ========================================================================

    public AnalyzedLesson Analyze(ExperimentTrace trace)
    {
        var patterns = ExtractPatterns(trace);
        var insight = GenerateInsight(trace, patterns);
        var recommendation = GenerateRecommendation(trace, patterns);
        var impact = ComputeImpact(trace);
        var generalizability = ComputeGeneralizability(trace);

        var lesson = new AnalyzedLesson
        {
            Summary = $"[{trace.Domain}] {(trace.Success ? "Success" : "Failure")}: {trace.Query[..Math.Min(trace.Query.Length, 60)]}",
            Insight = insight,
            Recommendation = recommendation,
            Domain = trace.Domain,
            TriggerPattern = patterns.PrimaryPattern,
            Impact = impact,
            Generalizability = generalizability,
            EvidenceIds = new List<string> { trace.Id }
        };

        _lessons[lesson.Id] = lesson;
        Interlocked.Increment(ref _totalLessons);

        _synapticMemory?.Store(new SynapticExperience
        {
            Query = trace.Query,
            Response = trace.Response,
            Label = trace.Success ? "success" : "failure",
            Confidence = trace.Confidence,
            Reward = trace.Reward,
            Metadata = $"analyzer_lesson={lesson.Id},domain={trace.Domain},route={trace.Route}",
            Type = SynapseType.Teaching
        });

        _evolutionStore?.RecordLesson(new EvolutionLesson
        {
            Category = $"Analyzer{(trace.Success ? "Success" : "Failure")}",
            Severity = impact,
            Summary = lesson.Summary,
            Mitigation = recommendation,
            SourceStage = "experiment_analyzer"
        });

        _logger.LogDebug("Analyzed: id={Id} domain={Domain} success={Success} impact={Impact:F2} gen={Gen:F2}",
            lesson.Id, trace.Domain, trace.Success, impact, generalizability);

        return lesson;
    }

    // ========================================================================
    // 3. Create/Reinforce Harness Interventions from Lessons
    // ========================================================================

    public void ApplyLessonToHarness(AnalyzedLesson lesson)
    {
        if (_harnessEvo == null || lesson.Impact < 0.3f) return;

        var interventionType = lesson.Domain switch
        {
            "code" => InterventionType.ProceduralSkill,
            "tools" => InterventionType.ActionRealization,
            _ => InterventionType.TrajectoryRegulation
        };

        _harnessEvo.Learn(
            $"lesson_{lesson.Id}_{lesson.TriggerPattern.GetHashCode():X}",
            lesson.TriggerPattern,
            interventionType,
            lesson.Recommendation);

        lesson.ReuseCount++;

        _logger.LogInformation("Applied lesson to harness: id={Id} type={Type} pattern={Pattern}",
            lesson.Id, interventionType, lesson.TriggerPattern[..Math.Min(lesson.TriggerPattern.Length, 40)]);
    }

    // ========================================================================
    // 4. Retrieve relevant lessons for a new query (UCB1-weighted)
    // ========================================================================

    public List<AnalyzedLesson> RetrieveRelevantLessons(string query, string domain, int topK = 5)
    {
        var candidates = _lessons.Values
            .Where(l => l.Domain == domain || l.Domain == "general" || domain == "general")
            .ToList();

        if (candidates.Count == 0) return new();

        return candidates
            .OrderByDescending(l =>
            {
                var relevance = ComputeTextSimilarity(query, l.Summary);
                var ucbScore = l.ReuseCount > 0
                    ? l.Impact + Math.Sqrt(2 * Math.Log(_totalExperiments + 1) / l.ReuseCount)
                    : l.Impact + 1.0;
                return relevance * 0.4f + ucbScore * 0.3f + l.Generalizability * 0.3f;
            })
            .Take(topK)
            .ToList();
    }

    // ========================================================================
    // 5. Batch analysis: periodic reflection over recent traces
    // ========================================================================

    public async Task<List<AnalyzedLesson>> BatchAnalyzeAsync(int? maxTraces = null)
    {
        var traces = _recentTraces.ToArray();
        var toAnalyze = maxTraces.HasValue ? traces.TakeLast(maxTraces.Value) : traces;
        var results = new List<AnalyzedLesson>();

        foreach (var trace in toAnalyze)
        {
            var lesson = Analyze(trace);
            ApplyLessonToHarness(lesson);
            results.Add(lesson);
        }

        if (results.Count > 0)
        {
            _logger.LogInformation("Batch analysis: {Count} traces → {Lessons} lessons",
                toAnalyze.Count(), results.Count);
        }

        return results;
    }

    // ========================================================================
    // 6. Domain statistics with MAP-Elites grid estimation
    // ========================================================================

    public Dictionary<string, double> GetDomainSuccessRates()
    {
        var result = new Dictionary<string, double>();
        lock (_domainSuccessCounts)
        {
            var allDomains = _domainSuccessCounts.Keys.Union(_domainFailureCounts.Keys);
            foreach (var d in allDomains)
            {
                var s = _domainSuccessCounts.GetValueOrDefault(d);
                var f = _domainFailureCounts.GetValueOrDefault(d);
                result[d] = (s + f) > 0 ? (double)s / (s + f) : 0;
            }
        }
        return result;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_experiments"] = _totalExperiments,
        ["total_lessons"] = _totalLessons,
        ["domain_rates"] = GetDomainSuccessRates(),
        ["active_lessons"] = _lessons.Count,
        ["recent_traces"] = _recentTraces.Count
    };

    // ========================================================================
    // Private analysis helpers
    // ========================================================================

    private static (string PrimaryPattern, List<string> Keywords) ExtractPatterns(ExperimentTrace trace)
    {
        var primary = trace.Domain switch
        {
            "code" => ExtractCodePattern(trace.Query),
            "tools" => trace.ToolSequence.Count > 0
                ? $"tool:{string.Join("->", trace.ToolSequence.Take(3))}"
                : "tool_execution",
            _ => trace.Query.Length > 30 ? trace.Query[..30] : trace.Query
        };

        var keywords = Regex.Matches(trace.Query, @"[a-zA-Z]{4,}|[\u4e00-\u9fff]{2,6}")
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .Take(5)
            .ToList();

        return (primary, keywords);
    }

    private static string ExtractCodePattern(string query)
    {
        var patterns = new[] { "read_file", "write_file", "execute", "debug", "build", "test", "refactor", "install" };
        foreach (var p in patterns)
            if (query.Contains(p, StringComparison.OrdinalIgnoreCase))
                return p;
        return "code";
    }

    private static string GenerateInsight(ExperimentTrace trace, (string, List<string>) patterns)
    {
        if (trace.Success)
        {
            return trace.Route switch
            {
                "local_llm" => $"L1 local model handles '{patterns.Item1}' tasks well (confidence={trace.Confidence:F2}, latency={trace.LatencyMs:F0}ms)",
                "delegate_l2" => $"L2 delegation effective for '{patterns.Item1}' at complexity {trace.Complexity:F2}",
                "cache_hit" => $"Cache hit pattern identified for '{patterns.Item1}'",
                "graph_knowledge" => $"Graph knowledge suffices for '{patterns.Item1}' queries",
                _ => $"Route '{trace.Route}' successful for domain pattern '{patterns.Item1}'"
            };
        }

        if (trace.Errors.Count > 0)
            return $"Failure in '{patterns.Item1}': {trace.Errors[0][..Math.Min(trace.Errors[0].Length, 100)]}";

        return trace.Confidence < 0.4f
            ? $"Low confidence ({trace.Confidence:F2}) on '{patterns.Item1}'. Consider upgrading route for complexity>{trace.Complexity:F2}."
            : $"Suboptimal route '{trace.Route}' for '{patterns.Item1}'. Confidence={trace.Confidence:F2}, Reward={trace.Reward:F2}";
    }

    private static string GenerateRecommendation(ExperimentTrace trace, (string, List<string>) patterns)
    {
        if (trace.Success)
        {
            return $"Reinforce: route={trace.Route}, pattern='{patterns.Item1}'. "
                + (trace.Route == "local_llm"
                    ? "Cache local LLM response. Increase confidence threshold for similar queries."
                    : $"Scale this pattern. Model={trace.ModelType}. Complexity threshold={trace.Complexity + 0.05f:F2}.");
        }

        return trace.Confidence < 0.4f && trace.Route != "delegate_l2"
            ? $"Route upgrade: redirect '{patterns.Item1}' queries to L2 (complexity>{trace.Complexity - 0.1f:F2})"
            : $"Add harness intervention for '{patterns.Item1}'. Verify tool outputs, increase grounding strictness.";
    }

    private static float ComputeImpact(ExperimentTrace trace)
    {
        var baseImpact = trace.Success ? 1.0f - trace.Complexity : trace.Complexity;
        baseImpact += trace.Confidence * 0.5f;
        baseImpact += trace.Reward * 0.3f;
        if (trace.ToolCallCount > 3) baseImpact += 0.2f;
        if (trace.Errors.Count > 0) baseImpact += 0.3f;
        return Math.Clamp(baseImpact / 2.3f, 0f, 1f);
    }

    private float ComputeGeneralizability(ExperimentTrace trace)
    {
        var rate = GetDomainSuccessRates().GetValueOrDefault(trace.Domain, 0.5);
        if (_subspaceAnalyzer != null)
        {
            var universal = _subspaceAnalyzer.GetUniversalSubspace();
            if (universal != null)
                return (float)(rate * 0.4 + universal.ExplainedVarianceRatio * 0.6);
        }
        return (float)rate;
    }

    private static double ComputeTextSimilarity(string a, string b)
    {
        var shorter = a.Length < b.Length ? a : b;
        var longer = a.Length < b.Length ? b : a;
        if (longer.Length == 0) return 1.0;
        var common = shorter.Count(c => longer.Contains(c, StringComparison.OrdinalIgnoreCase));
        return (double)common / longer.Length;
    }
}
