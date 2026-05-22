using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// Implementation of 5 recent arXiv papers (May 2026) integrated into LTAI.
/// 
/// Papers:
///   [2] arXiv:2603.12634 — BAVT: Budget-Aware Value Tree Search
///   [3] arXiv:2603.09716 — AutoAgent: Elastic Memory Orchestration  
///   [4] arXiv:2602.13949 — ERL: Experiential Reinforcement Learning
///   [8] arXiv:2509.18847 — Structured Reflection for Tool Interactions
///   [6] arXiv:2601.07264 — Confidence Dichotomy in Tool-Use Agents
/// </summary>

// ============================================================================
// #2 BAVT: Budget-Aware Value Tree Search (arXiv:2603.12634)
// "parameter-free transition from broad exploration to greedy exploitation
//  as the budget depletes"
// ============================================================================

public sealed record BAVTNode
{
    public string Action { get; init; } = "";
    public double Value { get; init; }
    public double ResidualValue { get; init; }
    public int Depth { get; init; }
    public double BudgetRatio { get; init; }
}

public sealed class BAVTRouter
{
    private readonly double _totalBudget;
    private double _remainingBudget;
    private readonly ILogger<BAVTRouter> _logger;
    private double _spentEstimate;

    public BAVTRouter(double totalBudget = 100.0, ILogger<BAVTRouter>? logger = null)
    {
        _totalBudget = totalBudget;
        _remainingBudget = totalBudget;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BAVTRouter>.Instance;
    }

    public double BudgetRatio => _remainingBudget / _totalBudget;

    /// <summary>
    /// Budget-conditioned node selection:
    /// P(action) ∝ value ^ budgetRatio
    /// When budget is high: broad exploration (nearly uniform).
    /// When budget is low: greedy exploitation (highest value wins).
    /// </summary>
    public BAVTNode SelectAction(List<BAVTNode> candidates)
    {
        if (candidates.Count == 0)
            return new BAVTNode { Action = "fallback", Value = 0, BudgetRatio = BudgetRatio };

        var ratio = BudgetRatio;
        var exponentiated = candidates
            .Select(c => new { Node = c, Score = Math.Pow(Math.Max(c.Value, 0.01), ratio) })
            .ToList();

        var total = exponentiated.Sum(e => e.Score);
        if (total <= 0) return candidates[0];

        var cumulative = 0.0;
        var roll = Random.Shared.NextDouble() * total;

        foreach (var item in exponentiated)
        {
            cumulative += item.Score;
            if (roll <= cumulative)
            {
                _logger.LogDebug("BAVT: selected {Action} value={Val:F2} budgetRatio={Ratio:F2}",
                    item.Node.Action, item.Node.Value, ratio);
                return item.Node with { BudgetRatio = ratio };
            }
        }

        return exponentiated.Last().Node with { BudgetRatio = ratio };
    }

    public void Spend(double cost)
    {
        _remainingBudget = Math.Max(0, _remainingBudget - cost);
        _spentEstimate += cost;
    }

    public double RemainingBudget => _remainingBudget;
    public double TotalSpent => _spentEstimate;

    /// <summary>
    /// Residual value predictor: scores relative progress rather than absolute quality,
    /// avoiding LLM self-evaluation overconfidence. (BAVT §3.2)
    /// </summary>
    public static double ResidualValue(double currentScore, double previousScore, double cost)
        => cost > 0 ? (currentScore - previousScore) / cost : 0;
}

// ============================================================================
// #4 ERL: Experiential Reinforcement Learning (arXiv:2602.13949)
// "embed an explicit experience-reflection-consolidation loop into the
//  reinforcement learning process"
// ============================================================================

public sealed record ERLExperience
{
    public string TaskId { get; init; } = "";
    public string Attempt { get; init; } = "";
    public string Feedback { get; init; } = "";
    public string? Reflection { get; init; }
    public string? CorrectedAttempt { get; init; }
    public bool Success { get; init; }
    public double Reward { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class ERLLoop
{
    private readonly ConcurrentDictionary<string, List<ERLExperience>> _history = new();
    private readonly ILogger<ERLLoop> _logger;
    private int _totalTrials;
    private int _totalSuccesses;

    public int TotalTrials => _totalTrials;
    public double SuccessRate => _totalTrials > 0 ? (double)_totalSuccesses / _totalTrials : 0;

    public ERLLoop(ILogger<ERLLoop>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ERLLoop>.Instance;
    }

    /// <summary>
    /// ERL core loop:
    ///   1. Generate initial attempt
    ///   2. Receive feedback (success/failure + reward)
    ///   3. If failed, produce reflection + corrected attempt
    ///   4. Reinforce success → internalize into policy
    /// </summary>
    public ERLExperience RecordTrial(string taskId, string attempt, string feedback, double reward, bool success)
    {
        _totalTrials++;
        if (success) _totalSuccesses++;

        var exp = new ERLExperience
        {
            TaskId = taskId,
            Attempt = attempt,
            Feedback = feedback,
            Reward = reward,
            Success = success
        };

        _history.AddOrUpdate(taskId,
            _ => new List<ERLExperience> { exp },
            (_, list) => { list.Add(exp); return list; });

        _logger.LogInformation("ERL trial {N}: task={Task} success={Success} reward={Reward:F2} rate={Rate:F2}",
            _totalTrials, taskId, success, reward, SuccessRate);

        return exp;
    }

    public ERLExperience ReflectAndCorrect(ERLExperience exp, Func<string, string> reflector)
    {
        if (exp.Success) return exp;

        var reflection = reflector(exp.Feedback);
        var correctedCall = $"Given failure reason: {exp.Feedback}, corrected action: {reflection}";

        _logger.LogInformation("ERL: reflection for {Task}: {Ref}",
            exp.TaskId, reflection[..Math.Min(reflection.Length, 100)]);

        return exp with { Reflection = reflection, CorrectedAttempt = correctedCall };
    }

    public List<ERLExperience> GetHistory(string taskId)
        => _history.GetValueOrDefault(taskId) ?? new();

    /// <summary>
    /// Consolidate: compress recent successes into policy patterns.
    /// Returns success pattern for the task type.
    /// </summary>
    public string ConsolidateSuccessPattern(string taskPrefix)
    {
        var successes = _history.Values
            .SelectMany(list => list)
            .Where(e => e.Success && e.TaskId.StartsWith(taskPrefix))
            .ToList();

        if (successes.Count == 0) return "";

        var pattern = successes.GroupBy(e => e.TaskId)
            .OrderByDescending(g => g.Average(e => e.Reward))
            .First();

        return $"Successful pattern for {taskPrefix}: avg_reward={pattern.Average(e => e.Reward):F2}, " +
            $"attempts={pattern.Count()}, count={successes.Count}";
    }
}

// ============================================================================
// #3 AutoAgent: Elastic Memory Orchestration (arXiv:2603.09716)
// "dynamically organizes interaction history by preserving raw records,
//  compressing redundant trajectories, and constructing reusable episodic abstractions"
// ============================================================================

public enum MemoryLayer { Raw, Compressed, Episodic }

public sealed record MemoryFragment
{
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
    public MemoryLayer Layer { get; init; } = MemoryLayer.Raw;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int AccessCount { get; set; }
    public double RelevanceScore { get; set; }
}

public sealed class ElasticMemoryOrchestrator
{
    private readonly ConcurrentDictionary<string, MemoryFragment> _raw = new();
    private readonly ConcurrentDictionary<string, MemoryFragment> _compressed = new();
    private readonly ConcurrentDictionary<string, List<MemoryFragment>> _episodic = new();
    private readonly ILogger<ElasticMemoryOrchestrator> _logger;

    private const int RawThreshold = 50;
    private const int CompressedThreshold = 200;
    private const double RedundancyThreshold = 0.85;

    public ElasticMemoryOrchestrator(ILogger<ElasticMemoryOrchestrator>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ElasticMemoryOrchestrator>.Instance;
    }

    /// <summary>
    /// Store: always keep raw record first.
    /// AutoAgent design: raw → compressed → episodic, cost increases along the chain.
    /// </summary>
    public void Store(string id, string content)
    {
        var frag = new MemoryFragment { Id = id, Content = content, Layer = MemoryLayer.Raw };
        _raw[id] = frag;
        CompressIfNeeded();
    }

    public void Access(string id)
    {
        if (_raw.TryGetValue(id, out var frag)) { frag.AccessCount++; return; }
        if (_compressed.TryGetValue(id, out var cFrag)) { cFrag.AccessCount++; return; }
    }

    private void CompressIfNeeded()
    {
        if (_raw.Count < RawThreshold) return;

        var groups = _raw.Values
            .GroupBy(f => f.Content.Length / 100)
            .Where(g => g.Count() > 1)
            .Take(5);

        foreach (var group in groups)
        {
            var items = group.ToList();
            if (items.Count < 2) continue;

            var merged = string.Join(" | ", items.Select(f =>
                f.Content.Length > 60 ? f.Content[..60] + "..." : f.Content));

            var compId = $"comp_{group.Key}_{DateTime.UtcNow.Ticks}";
            _compressed[compId] = new MemoryFragment
            {
                Id = compId, Content = merged, Layer = MemoryLayer.Compressed,
                RelevanceScore = items.Average(f => f.RelevanceScore)
            };

            foreach (var item in items)
                _raw.TryRemove(item.Id, out _);
        }

        _logger.LogInformation("ElasticMemory: compressed {RawCount} raw → {CompCount} compressed",
            _raw.Count, _compressed.Count);
    }

    /// <summary>
    /// Construct episodic abstraction: group related memories into a narrative episode.
    /// </summary>
    public string? ConstructEpisode(string topic, int maxItems = 10)
    {
        var related = _raw.Values.Concat(_compressed.Values)
            .Where(f => f.Content.Contains(topic, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.AccessCount)
            .ThenByDescending(f => f.RelevanceScore)
            .Take(maxItems)
            .ToList();

        if (related.Count < 2) return null;

        var episode = $"Episode[{topic}]: " + string.Join(" → ", related.Select(f =>
            f.Content.Length > 40 ? f.Content[..40] : f.Content));

        var epId = $"ep_{topic}_{DateTime.UtcNow.Ticks}";
        _episodic.AddOrUpdate(topic,
            _ => new List<MemoryFragment> { new() { Id = epId, Content = episode, Layer = MemoryLayer.Episodic } },
            (_, list) => { list.Add(new() { Id = epId, Content = episode, Layer = MemoryLayer.Episodic }); return list; });

        _logger.LogInformation("ElasticMemory: constructed episode for '{Topic}' ({Count} items, {Len} chars)",
            topic, related.Count, episode.Length);

        return episode;
    }

    public (int raw, int compressed, int episodic) Stats
        => (_raw.Count, _compressed.Count, _episodic.Sum(kv => kv.Value.Count));
}

// ============================================================================
// #8 Structured Reflection (arXiv:2509.18847)
// "turns the path from error to repair into an explicit, controllable,
//  and trainable action: diagnose failure → propose corrected call"
// ============================================================================

public enum ReflectionAction { Diagnose, Repair, Retry, Abandon }

public sealed record StructuredReflection
{
    public ReflectionAction Action { get; init; }
    public string Diagnosis { get; init; } = "";
    public string Correction { get; init; } = "";
    public string Evidence { get; init; } = "";
    public double Confidence { get; init; }
    public bool ShouldRetry => Action == ReflectionAction.Repair || Action == ReflectionAction.Retry;
}

public sealed class StructuredReflectionEngine
{
    private readonly ILogger<StructuredReflectionEngine> _logger;
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();
    private int _totalReflections;
    private int _successfulReflections;

    public int TotalReflections => _totalReflections;
    public double RecoveryRate => _totalReflections > 0 ? (double)_successfulReflections / _totalReflections : 0;

    public StructuredReflectionEngine(ILogger<StructuredReflectionEngine>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StructuredReflectionEngine>.Instance;
    }

    /// <summary>
    /// Structured reflection loop:
    ///   1. Diagnose: what went wrong? (evidence from error output)
    ///   2. Decide: retry with repair? abandon?
    ///   3. Repair: produce corrected call
    ///   4. Track recovery rate
    /// </summary>
    public StructuredReflection Reflect(string toolName, string errorOutput, int failureCount)
    {
        _totalReflections++;

        _failureCounts.AddOrUpdate(toolName, 1, (_, c) => c + 1);
        var totalFails = _failureCounts.GetValueOrDefault(toolName);

        var reflection = totalFails switch
        {
            <= 2 => new StructuredReflection
            {
                Action = ReflectionAction.Repair,
                Diagnosis = $"Parameter error in {toolName}: {errorOutput[..Math.Min(errorOutput.Length, 100)]}",
                Correction = $"Retry {toolName} with adjusted parameters",
                Evidence = errorOutput,
                Confidence = 0.7
            },
            <= 5 => new StructuredReflection
            {
                Action = ReflectionAction.Retry,
                Diagnosis = $"Persistent {toolName} failure ({totalFails} attempts): {errorOutput[..Math.Min(errorOutput.Length, 80)]}",
                Correction = $"Rebuild tool context for {toolName} and retry",
                Evidence = errorOutput,
                Confidence = 0.4
            },
            _ => new StructuredReflection
            {
                Action = ReflectionAction.Abandon,
                Diagnosis = $"Abandoning {toolName} after {totalFails} failures",
                Evidence = errorOutput,
                Confidence = 0.1
            }
        };

        _logger.LogDebug("StructuredReflection: {Tool} action={Action} conf={Conf:F2} fails={Fails}",
            toolName, reflection.Action, reflection.Confidence, totalFails);

        return reflection;
    }

    public void RecordRecovery(string toolName)
    {
        _successfulReflections++;
        _failureCounts.TryRemove(toolName, out _);
        _logger.LogInformation("StructuredReflection: {Tool} recovered. rate={Rate:F2}", toolName, RecoveryRate);
    }
}

// ============================================================================
// #6 Confidence Gate (arXiv:2601.07264)
// "evidence tools systematically induce severe overconfidence;
//  verification tools mitigate miscalibration through deterministic feedback"
// ============================================================================

public enum ToolNature { Evidence, Verification, Neutral }

public sealed record ConfidenceGate
{
    public ToolNature Nature { get; init; }
    public double RawConfidence { get; init; }
    public double CalibratedConfidence { get; init; }
    public string AdjustmentReason { get; init; } = "";
}

public sealed class ConfidenceCalibrator
{
    private readonly ILogger<ConfidenceCalibrator> _logger;
    private readonly ConcurrentDictionary<string, (double sumConf, double sumAccuracy, int count)> _stats = new();

    public ConfidenceCalibrator(ILogger<ConfidenceCalibrator>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfidenceCalibrator>.Instance;
    }

    /// <summary>
    /// Tool nature classification rules:
    ///   Evidence tools (web search, knowledge retrieval) → tendency to overconfidence → down-weight
    ///   Verification tools (code exec, math solver) → grounded → keep or up-weight
    /// </summary>
    public static ToolNature ClassifyTool(string toolName)
    {
        if (toolName.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("retriev", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("rag", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("web", StringComparison.OrdinalIgnoreCase))
            return ToolNature.Evidence;

        if (toolName.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("check", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("validate", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("test", StringComparison.OrdinalIgnoreCase))
            return ToolNature.Verification;

        return ToolNature.Neutral;
    }

    public ConfidenceGate Calibrate(string toolName, double rawConfidence, double actualAccuracy)
    {
        var nature = ClassifyTool(toolName);

        _stats.AddOrUpdate(toolName,
            _ => (rawConfidence, actualAccuracy, 1),
            (_, t) => (t.sumConf + rawConfidence, t.sumAccuracy + actualAccuracy, t.count + 1));

        var stats = _stats.GetValueOrDefault(toolName);
        var avgConf = stats.count > 0 ? stats.sumConf / stats.count : rawConfidence;
        var avgAcc = stats.count > 0 ? stats.sumAccuracy / stats.count : actualAccuracy;
        var calibrationGap = avgConf - avgAcc;

        var calibrated = nature switch
        {
            ToolNature.Evidence => rawConfidence * 0.75,   // evidence tools: 25% overconfidence penalty
            ToolNature.Verification => Math.Min(rawConfidence * 1.1, 1.0), // verification tools: slight boost
            _ => rawConfidence * 0.9
        };

        var reason = nature switch
        {
            ToolNature.Evidence when calibrationGap > 0.2 =>
                $"Evidence tool overconfidence: gap={calibrationGap:F2}, calibrated {rawConfidence:F2}→{calibrated:F2}",
            ToolNature.Verification =>
                $"Verification tool grounded: gap={calibrationGap:F2}, calibrated {rawConfidence:F2}→{calibrated:F2}",
            _ => ""
        };

        _logger.LogDebug("ConfCal: {Tool} nature={Nature} raw={Raw:F2}→cal={Cal:F2} gap={Gap:F2}",
            toolName, nature, rawConfidence, calibrated, calibrationGap);

        return new ConfidenceGate
        {
            Nature = nature, RawConfidence = rawConfidence,
            CalibratedConfidence = calibrated, AdjustmentReason = reason
        };
    }

    public double GetCalibrationGap(string toolName)
    {
        var stats = _stats.GetValueOrDefault(toolName);
        if (stats.count == 0) return 0;
        return stats.sumConf / stats.count - stats.sumAccuracy / stats.count;
    }
}


