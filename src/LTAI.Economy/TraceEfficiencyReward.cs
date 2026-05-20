using LTAI.Core.System;

namespace LTAI.Economy;

public sealed class TraceEfficiencyReward
{
    private double _referenceCost;
    private readonly double _tighteningRate;
    private readonly double _minReference;
    private int _updateCounter;
    private readonly object _lock = new();

    public TraceEfficiencyReward(double initialReference = 1.0, double tighteningRate = 0.95, double minReference = 0.15)
    {
        _referenceCost = initialReference;
        _tighteningRate = tighteningRate;
        _minReference = minReference;
    }

    public double ComputeTraceCost(InteractionTrajectory trajectory)
    {
        double tokenCost = 0;
        int toolRounds = 0;

        foreach (var step in trajectory.Steps)
        {
            tokenCost += step.Thought.Length * 0.25;
            if (step.Action != null)
                toolRounds++;
            if (step.Observation != null)
                tokenCost += step.Observation.Length * 0.25;
        }

        return tokenCost + 2.0 * toolRounds;
    }

    public (double efficiencyReward, double costPenalty) ComputeEfficiencyReward(InteractionTrajectory trajectory)
    {
        var cost = ComputeTraceCost(trajectory);
        double efficiencyReward;

        lock (_lock)
        {
            if (cost <= _referenceCost)
                efficiencyReward = 1.0;
            else if (cost > _referenceCost * 4)
                efficiencyReward = -0.3;
            else
            {
                var ratio = cost / _referenceCost;
                efficiencyReward = 1.0 - (ratio - 1.0) * 0.5;
            }

            efficiencyReward = Math.Clamp(efficiencyReward, -0.3, 1.0);
        }

        var costPenalty = -Math.Log(Math.Max(cost, 1)) * 0.01;
        return (efficiencyReward, costPenalty);
    }

    public void TightenReference(List<InteractionTrajectory> trajectories)
    {
        if (trajectories.Count == 0) return;

        var completedCosts = trajectories
            .Where(t => t.Completed && t.TotalReward >= 0.3)
            .Select(ComputeTraceCost)
            .ToList();

        if (completedCosts.Count < 2) return;

        var minCost = completedCosts.Min();
        var medianCost = completedCosts.OrderBy(c => c).ElementAt(completedCosts.Count / 2);

        var targetCost = minCost * 0.6 + medianCost * 0.4;

        lock (_lock)
        {
            _updateCounter++;
            _referenceCost = Math.Max(
                _minReference,
                _referenceCost * _tighteningRate + targetCost * (1.0 - _tighteningRate));
        }
    }

    public double ComputeCostNormalizedReward(InteractionTrajectory trajectory)
    {
        var (efficiencyReward, costPenalty) = ComputeEfficiencyReward(trajectory);
        var baseReward = trajectory.TotalReward;
        return baseReward * (0.7 + 0.3 * efficiencyReward) + costPenalty;
    }

    public double ComputeAdvantageWithCost(
        Dictionary<string, double> advantages,
        InteractionTrajectory trajectory,
        double costWeight = 0.3)
    {
        if (!advantages.TryGetValue(trajectory.TrajectoryId, out var advantage))
            return 0;

        var (efficiencyReward, _) = ComputeEfficiencyReward(trajectory);

        return advantage * (1.0 - costWeight) + efficiencyReward * costWeight;
    }

    public double ComputeCAS(InteractionTrajectory trajectory)
    {
        var accuracy = trajectory.Completed ? trajectory.TotalReward : trajectory.TotalReward * 0.5;
        var tokens = trajectory.Steps.Sum(s => (s.Thought.Length + (s.Observation?.Length ?? 0)) * 0.25);
        var toolRounds = trajectory.Steps.Count(s => s.Action != null);
        var denominator = Math.Max(1, tokens + 2.0 * toolRounds + 1);
        return accuracy * accuracy * 100.0 / denominator;
    }

    public Dictionary<string, object> GetStats() {
        lock (_lock) {
            return new() {
                ["reference_cost"] = Math.Round(_referenceCost, 3),
                ["tightening_rate"] = _tighteningRate,
                ["min_reference"] = _minReference,
                ["updates"] = _updateCounter
            };
        }
    }
}
