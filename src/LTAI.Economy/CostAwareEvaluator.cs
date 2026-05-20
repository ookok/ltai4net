using LTAI.Core.System;

namespace LTAI.Economy;

public sealed record CostAwareEvaluation
{
    public double Accuracy { get; init; }
    public double CAS { get; init; }
    public double TokenCost { get; init; }
    public int ToolRounds { get; init; }
    public double AvgReward { get; init; }
    public double CostNormalizedReward { get; init; }
    public double ParetoScore { get; init; }
    public string Verdict { get; init; } = "";
    public Dictionary<string, double> BreakdownMetrics { get; init; } = new();
}

public sealed class CostAwareEvaluator
{
    private readonly TraceEfficiencyReward _traceReward;
    private readonly List<CostAwareEvaluation> _evaluationHistory = new();
    private const int MaxHistory = 500;
    private readonly object _lock = new();

    public CostAwareEvaluator(TraceEfficiencyReward? traceReward = null)
    {
        _traceReward = traceReward ?? new TraceEfficiencyReward();
    }

    public CostAwareEvaluation Evaluate(InteractionTrajectory trajectory)
    {
        var accuracy = trajectory.Completed ? Math.Clamp(trajectory.TotalReward, 0, 1) : trajectory.TotalReward * 0.5;
        var tokenCost = trajectory.Steps.Sum(s => (s.Thought.Length + (s.Observation?.Length ?? 0)) * 0.25);
        var toolRounds = trajectory.Steps.Count(s => s.Action != null);

        var cas = accuracy * accuracy * 100.0 / Math.Max(1, tokenCost + 2.0 * toolRounds + 1);
        var costNormalizedReward = _traceReward.ComputeCostNormalizedReward(trajectory);

        var paretoScore = ComputeParetoScore(accuracy, tokenCost, toolRounds);

        var verdict = cas switch
        {
            >= 50 => "excellent_efficiency",
            >= 20 => "good_efficiency",
            >= 5 => "acceptable",
            >= 1 => "low_efficiency",
            _ => "poor_efficiency"
        };

        var breakdown = new Dictionary<string, double>
        {
            ["accuracy"] = Math.Round(accuracy, 3),
            ["token_cost"] = Math.Round(tokenCost, 1),
            ["tool_rounds"] = toolRounds,
            ["cost_per_step"] = trajectory.StepCount > 0 ? Math.Round(tokenCost / trajectory.StepCount, 1) : 0,
            ["latency_ms"] = trajectory.ElapsedMs,
            ["steps"] = trajectory.StepCount,
            ["completed"] = trajectory.Completed ? 1 : 0
        };

        var eval = new CostAwareEvaluation
        {
            Accuracy = Math.Round(accuracy, 3),
            CAS = Math.Round(cas, 3),
            TokenCost = Math.Round(tokenCost, 1),
            ToolRounds = toolRounds,
            AvgReward = Math.Round(trajectory.TotalReward, 3),
            CostNormalizedReward = Math.Round(costNormalizedReward, 3),
            ParetoScore = Math.Round(paretoScore, 3),
            Verdict = verdict,
            BreakdownMetrics = breakdown
        };

        lock (_lock)
        {
            _evaluationHistory.Add(eval);
            if (_evaluationHistory.Count > MaxHistory) _evaluationHistory.RemoveAt(0);
        }

        return eval;
    }

    public Dictionary<string, object> EvaluateBatch(List<InteractionTrajectory> trajectories)
    {
        if (trajectories.Count == 0)
            return new() { ["cas"] = 0.0, ["accuracy"] = 0.0 };

        var evals = trajectories.Select(Evaluate).ToList();

        var avgAccuracy = evals.Average(e => e.Accuracy);
        var avgCAS = evals.Average(e => e.CAS);
        var totalTokens = evals.Sum(e => e.TokenCost);
        var totalToolRounds = evals.Sum(e => e.ToolRounds);
        var avgPareto = evals.Average(e => e.ParetoScore);

        return new()
        {
            ["avg_accuracy"] = Math.Round(avgAccuracy, 3),
            ["avg_cas"] = Math.Round(avgCAS, 3),
            ["total_tokens"] = Math.Round(totalTokens, 1),
            ["total_tool_rounds"] = totalToolRounds,
            ["avg_pareto_score"] = Math.Round(avgPareto, 3),
            ["trajectory_count"] = trajectories.Count,
            ["verdict_distribution"] = evals
                .GroupBy(e => e.Verdict)
                .ToDictionary(g => g.Key, g => g.Count()),
            ["cost_reference"] = _traceReward.GetStats()
        };
    }

    public double ComputeCAS(InteractionTrajectory trajectory)
    {
        return _traceReward.ComputeCAS(trajectory);
    }

    public List<Dictionary<string, object>> GetParetoFrontier(int topN = 20)
    {
        lock (_lock)
        {
            return _evaluationHistory
                .OrderByDescending(e => e.ParetoScore)
                .Take(topN)
                .Select(e => new Dictionary<string, object>
                {
                    ["accuracy"] = e.Accuracy,
                    ["cas"] = e.CAS,
                    ["token_cost"] = e.TokenCost,
                    ["tool_rounds"] = e.ToolRounds,
                    ["pareto_score"] = e.ParetoScore,
                    ["verdict"] = e.Verdict
                })
                .ToList();
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var recent = _evaluationHistory.TakeLast(100).ToList();
            return new()
            {
                ["total_evaluations"] = _evaluationHistory.Count,
                ["avg_cas"] = Math.Round(recent.Count > 0 ? recent.Average(e => e.CAS) : 0, 3),
                ["avg_accuracy"] = Math.Round(recent.Count > 0 ? recent.Average(e => e.Accuracy) : 0, 3),
                ["avg_token_cost"] = Math.Round(recent.Count > 0 ? recent.Average(e => e.TokenCost) : 0, 1),
                ["excellent_rate"] = recent.Count > 0
                    ? Math.Round((double)recent.Count(e => e.Verdict == "excellent_efficiency") / recent.Count, 3)
                    : 0,
                ["trace_reference"] = _traceReward.GetStats()["reference_cost"]
            };
        }
    }

    private static double ComputeParetoScore(double accuracy, double tokenCost, int toolRounds)
    {
        var normalizedAcc = accuracy * 100;
        var normalizedCost = Math.Log(Math.Max(1, tokenCost + 2.0 * toolRounds + 1));
        return normalizedAcc / normalizedCost;
    }

    public void RecordTrajectory(InteractionTrajectory trajectory)
    {
        var eval = Evaluate(trajectory);
        lock (_lock)
        {
            _evaluationHistory.Add(eval);
            if (_evaluationHistory.Count > MaxHistory) _evaluationHistory.RemoveAt(0);
        }
    }
}
