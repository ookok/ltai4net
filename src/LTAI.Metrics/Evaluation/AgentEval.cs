using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Metrics.Evaluation;

public sealed record OutputEval(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("feedback")] string Feedback,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp
);

public sealed record TraceEval(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("turns")] int Turns,
    [property: JsonPropertyName("converged")] bool Converged,
    [property: JsonPropertyName("loop_detected")] bool LoopDetected,
    [property: JsonPropertyName("avg_turn_depth")] double AvgTurnDepth,
    [property: JsonPropertyName("score")] double Score
);

public sealed record ComponentMetric(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("success_rate")] double SuccessRate,
    [property: JsonPropertyName("p50_ms")] double P50Ms,
    [property: JsonPropertyName("p95_ms")] double P95Ms,
    [property: JsonPropertyName("p99_ms")] double P99Ms,
    [property: JsonPropertyName("total_calls")] int TotalCalls,
    [property: JsonPropertyName("last_eval")] DateTime LastEval
);

public sealed record DriftReport(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("baseline_score")] double BaselineScore,
    [property: JsonPropertyName("current_score")] double CurrentScore,
    [property: JsonPropertyName("drift_pct")] double DriftPct,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("alert")] bool Alert,
    [property: JsonPropertyName("message")] string Message
);

public sealed record EvalCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("expected")] string Expected,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("difficulty")] string Difficulty,
    [property: JsonPropertyName("tags")] List<string> Tags
);

public sealed record EvalDataset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cases")] List<EvalCase> Cases,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("version")] int Version
);

public sealed record EvalResult(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("feedback")] string Feedback,
    [property: JsonPropertyName("latency_ms")] double LatencyMs
);

public sealed record DatasetResult(
    [property: JsonPropertyName("dataset_id")] string DatasetId,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("total_cases")] int TotalCases,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("avg_score")] double AvgScore,
    [property: JsonPropertyName("pass_rate")] double PassRate,
    [property: JsonPropertyName("results")] List<EvalResult> Results,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp
);

public sealed record CaseComparison(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("score_before")] double ScoreBefore,
    [property: JsonPropertyName("score_after")] double ScoreAfter,
    [property: JsonPropertyName("improved")] bool Improved
);

public sealed record ComparisonReport(
    [property: JsonPropertyName("before_id")] string BeforeId,
    [property: JsonPropertyName("after_id")] string AfterId,
    [property: JsonPropertyName("avg_score_before")] double AvgScoreBefore,
    [property: JsonPropertyName("avg_score_after")] double AvgScoreAfter,
    [property: JsonPropertyName("score_delta")] double ScoreDelta,
    [property: JsonPropertyName("improved_cases")] int ImprovedCases,
    [property: JsonPropertyName("regressed_cases")] int RegressedCases,
    [property: JsonPropertyName("new_passes")] int NewPasses,
    [property: JsonPropertyName("new_failures")] int NewFailures
);

public sealed class AgentEval
{
    private static readonly Lazy<AgentEval> _instance = new(() => new AgentEval());
    public static AgentEval Instance => _instance.Value;

    private readonly ILogger<AgentEval> _logger;

    private readonly ConcurrentDictionary<string, OutputEval> _outputEvals = new();
    private readonly ConcurrentDictionary<string, TraceEval> _traceEvals = new();
    private readonly ConcurrentDictionary<string, ComponentMetric> _componentMetrics = new();
    private readonly ConcurrentDictionary<string, EvalDataset> _datasets = new();
    private readonly ConcurrentDictionary<string, DatasetResult> _runResults = new();
    private readonly ConcurrentDictionary<string, List<double>> _baselineScores = new();
    private readonly ConcurrentDictionary<string, List<double>> _toolLatencies = new();
    private readonly List<double> _driftHistory = new();
    private readonly object _lock = new();

    public AgentEval() : this(NullLogger<AgentEval>.Instance) { }

    public AgentEval(ILogger<AgentEval> logger)
    {
        _logger = logger;
    }

    public OutputEval EvalOutput(string agent, string task, string output, string? expected, string? reference)
    {
        double score;
        string feedback;

        if (expected is not null)
        {
            var outputWords = SplitWords(output);
            var expectedWords = SplitWords(expected);
            var intersection = outputWords.Intersect(expectedWords).Count();
            var union = outputWords.Union(expectedWords).Count();
            score = union > 0 ? Math.Min(1.0, (double)intersection / union) : 0;

            feedback = $"Word overlap: {intersection}/{union} = {score:F2}";
        }
        else if (reference is not null)
        {
            score = Math.Min(1.0, output.Length / 500.0);
            feedback = $"Length-based score: {output.Length} chars => {score:F2}";
        }
        else
        {
            score = Math.Min(1.0, output.Length / 500.0);
            feedback = $"No expected or reference; length-based: {score:F2}";
        }

        var level = score >= 0.8 ? "PASS" : score >= 0.5 ? "WARN" : "FAIL";

        var result = new OutputEval(agent, task, score, level, feedback, DateTime.UtcNow);

        var key = $"{agent}_{task}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        _outputEvals[key] = result;

        PruneDictionary(_outputEvals, 50);

        _logger.LogTrace("EvalOutput agent={Agent} task={Task} score={Score:F2} level={Level}", agent, task, score, level);

        return result;
    }

    public TraceEval EvalTrace(string agent, int turns, bool hasRepeatedPatterns, double avgTurnDepth)
    {
        var loopDetected = turns > 30 || hasRepeatedPatterns;

        double score = loopDetected ? 0.2
            : turns > 15 ? 0.6
            : turns > 10 ? 0.8
            : 1.0;

        if (avgTurnDepth < 1.0)
            score *= 0.7;

        var converged = score > 0.7;

        var result = new TraceEval(agent, turns, converged, loopDetected, avgTurnDepth, score);

        var key = $"{agent}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        _traceEvals[key] = result;

        PruneDictionary(_traceEvals, 50);

        _logger.LogTrace("EvalTrace agent={Agent} turns={Turns} score={Score:F2} converged={Converged} loop={LoopDetected}",
            agent, turns, score, converged, loopDetected);

        return result;
    }

    public ComponentMetric EvalComponent(string tool, bool success, double latencyMs)
    {
        var latencies = _toolLatencies.GetOrAdd(tool, _ => new List<double>());
        double oldRate;
        int totalCalls;
        ComponentMetric metric;

        lock (_lock)
        {
            latencies.Add(latencyMs);
            while (latencies.Count > 200)
                latencies.RemoveAt(0);

            var existing = _componentMetrics.TryGetValue(tool, out var m);
            oldRate = existing ? m!.SuccessRate : 0.0;
            totalCalls = existing ? m!.TotalCalls : 0;

            var newRate = (oldRate * totalCalls + (success ? 1.0 : 0.0)) / (totalCalls + 1);
            totalCalls++;

            var sorted = latencies.OrderBy(x => x).ToList();
            int n = sorted.Count;
            double p50 = sorted[Math.Max(0, n * 50 / 100 - 1)];
            double p95 = sorted[Math.Max(0, n * 95 / 100 - 1)];
            double p99 = sorted[Math.Max(0, n * 99 / 100 - 1)];

            metric = new ComponentMetric(tool, newRate, p50, p95, p99, totalCalls, DateTime.UtcNow);
            _componentMetrics[tool] = metric;
        }

        _logger.LogTrace("EvalComponent tool={Tool} success={Success} latency={Latency:F1}ms rate={Rate:F3}",
            tool, success, latencyMs, metric.SuccessRate);

        return metric;
    }

    public DriftReport CheckDrift(string agent, double threshold = 10.0)
    {
        var scores = _baselineScores.GetOrAdd(agent, _ => new List<double>());

        lock (_lock)
        {
            while (scores.Count >= 200)
                scores.RemoveAt(0);
        }

        if (scores.Count < 2)
        {
            return new DriftReport(agent, 0, 0, 0, threshold, false, $"Agent '{agent}' has fewer than 2 scores logged; cannot compute drift");
        }

        var mid = scores.Count / 2;
        var firstHalf = scores.Take(mid).ToList();
        var secondHalf = scores.Skip(mid).ToList();

        var baselineScore = firstHalf.Count > 0 ? firstHalf.Average() : 0;
        var currentScore = secondHalf.Count > 0 ? secondHalf.Average() : 0;

        var driftPct = baselineScore > 0.01
            ? (currentScore - baselineScore) / baselineScore * 100.0
            : 0;

        var alert = Math.Abs(driftPct) > threshold;

        var message = alert
            ? $"ALERT: Agent '{agent}' drifted by {driftPct:F2}% (threshold {threshold}%); baseline={baselineScore:F4}, current={currentScore:F4}"
            : $"Agent '{agent}' drift {driftPct:F2}% within threshold {threshold}%";

        _logger.LogTrace("CheckDrift agent={Agent} baseline={Baseline:F3} current={Current:F3} drift={Drift:F2}% alert={Alert}",
            agent, baselineScore, currentScore, driftPct, alert);

        return new DriftReport(agent, baselineScore, currentScore, driftPct, threshold, alert, message);
    }

    public void SaveDataset(EvalDataset dataset)
    {
        _datasets[dataset.Id] = dataset;
        _logger.LogTrace("SaveDataset id={Id} name={Name} cases={CaseCount}", dataset.Id, dataset.Name, dataset.Cases.Count);
    }

    public EvalDataset? LoadDataset(string datasetId)
    {
        _datasets.TryGetValue(datasetId, out var dataset);
        return dataset;
    }

    public List<EvalDataset> ListDatasets()
    {
        return _datasets.Values.ToList();
    }

    public bool DeleteDataset(string datasetId)
    {
        var removed = _datasets.TryRemove(datasetId, out _);
        _logger.LogTrace("DeleteDataset id={Id} removed={Removed}", datasetId, removed);
        return removed;
    }

    public void RecordRunResult(DatasetResult result)
    {
        _runResults[result.DatasetId] = result;
        _logger.LogTrace("RecordRunResult dataset={DatasetId} agent={Agent} avgScore={AvgScore:F2} passRate={PassRate:F2}%",
            result.DatasetId, result.Agent, result.AvgScore, result.PassRate);
    }

    public ComparisonReport? CompareRuns(string runAId, string runBId)
    {
        if (!_runResults.TryGetValue(runAId, out var runA)) return null;
        if (!_runResults.TryGetValue(runBId, out var runB)) return null;

        var beforeMap = runA.Results.ToDictionary(r => r.CaseId);
        var afterMap = runB.Results.ToDictionary(r => r.CaseId);

        int improvedCases = 0, regressedCases = 0, newPasses = 0, newFailures = 0;

        foreach (var kv in afterMap)
        {
            if (!beforeMap.TryGetValue(kv.Key, out var before)) continue;

            if (kv.Value.Score > before.Score) improvedCases++;
            else if (kv.Value.Score < before.Score) regressedCases++;

            if (before.Level is "FAIL" or "WARN" && kv.Value.Level == "PASS") newPasses++;
            else if (before.Level == "PASS" && kv.Value.Level is "FAIL" or "WARN") newFailures++;
        }

        var avgBefore = runA.AvgScore;
        var avgAfter = runB.AvgScore;

        return new ComparisonReport(runAId, runBId, avgBefore, avgAfter, avgAfter - avgBefore,
            improvedCases, regressedCases, newPasses, newFailures);
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["output_evals"] = _outputEvals.Count,
            ["trace_evals"] = _traceEvals.Count,
            ["component_tools"] = _componentMetrics.Keys.ToList(),
            ["dataset_count"] = _datasets.Count,
            ["run_count"] = _runResults.Count
        };
    }

    public void LogScore(string agent, double score)
    {
        var scores = _baselineScores.GetOrAdd(agent, _ => new List<double>());

        lock (_lock)
        {
            scores.Add(score);
            while (scores.Count > 200)
                scores.RemoveAt(0);
        }

        lock (_lock)
        {
            _driftHistory.Add(score);
            while (_driftHistory.Count > 200)
                _driftHistory.RemoveAt(0);
        }
    }

    private void PruneDictionary(ConcurrentDictionary<string, OutputEval> dict, int maxCount)
    {
        lock (_lock)
        {
            while (dict.Count > maxCount)
            {
                var oldest = dict.OrderBy(kv => kv.Value.Timestamp).First();
                dict.TryRemove(oldest.Key, out _);
            }
        }
    }

    private void PruneDictionary(ConcurrentDictionary<string, TraceEval> dict, int maxCount)
    {
        lock (_lock)
        {
            while (dict.Count > maxCount)
            {
                var oldest = dict.OrderBy(kv => kv.Value.Score).First();
                dict.TryRemove(oldest.Key, out _);
            }
        }
    }

    private static HashSet<string> SplitWords(string text)
    {
        return text
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '_', '/', '\\' },
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
    }
}
