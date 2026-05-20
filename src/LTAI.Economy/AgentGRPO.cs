using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed class AgentGRPO
{
    private readonly IInteractionLoop _interactionLoop;
    private readonly SessionResilience _sessionResilience;
    private readonly TraceEfficiencyReward? _traceReward;
    private readonly OnPolicyDistillation? _opd;
    private readonly CostAwareEvaluator? _costEvaluator;
    private readonly ILogger<AgentGRPO>? _logger;

    private readonly List<InteractionTrajectory> _trajectoryHistory = new();
    private readonly Dictionary<string, double> _toolPreferences = new();
    private readonly Dictionary<string, (double mean, double std)> _stepRewardStats = new();
    private const double LearningRate = 0.02;
    private const int MaxTrajectoryHistory = 200;
    private readonly object _lock = new();

    public AgentGRPO(
        IInteractionLoop interactionLoop,
        SessionResilience? sessionResilience = null,
        TraceEfficiencyReward? traceReward = null,
        OnPolicyDistillation? opd = null,
        CostAwareEvaluator? costEvaluator = null,
        ILogger<AgentGRPO>? logger = null)
    {
        _interactionLoop = interactionLoop;
        _sessionResilience = sessionResilience ?? SessionResilience.Instance;
        _traceReward = traceReward;
        _opd = opd;
        _costEvaluator = costEvaluator;
        _logger = logger;
    }

    public async Task<GRPOTrainingResult> TrainAsync(
        IReadOnlyList<string> tasks,
        RolloutConfig? config = null,
        int epochs = 3,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new RolloutConfig();
        var sw = Stopwatch.StartNew();
        var allTrajectories = new List<InteractionTrajectory>();
        double bestReward = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trajectories = new List<InteractionTrajectory>();
            await foreach (var traj in _interactionLoop.RunBatchAsync(tasks, cfg, cancellationToken))
            {
                trajectories.Add(traj);
                allTrajectories.Add(traj);
            }

            foreach (var traj in trajectories)
            {
                UpdateToolPreferences(traj);
                UpdateStepRewardStats(traj);
            }

            var groupAdvantage = ComputeGroupAdvantage(trajectories);
            ApplyPolicyGradient(trajectories, groupAdvantage);

            _traceReward?.TightenReference(trajectories);

            var avgReward = trajectories.Count > 0 ? trajectories.Average(t => t.TotalReward) : 0;
            if (avgReward > bestReward) bestReward = avgReward;

            StoreTrajectories(trajectories);

            _logger?.LogInformation(
                "AgentGRPO epoch={Epoch}: avgReward={AvgReward:F3} trajectories={Count} costRef={Ref}",
                epoch + 1, avgReward, trajectories.Count,
                _traceReward?.GetStats().GetValueOrDefault("reference_cost", 0) ?? 0);
        }

        sw.Stop();

        var finalAvg = allTrajectories.Count > 0
            ? allTrajectories.Average(t => t.TotalReward)
            : 0;

        var metrics = GetMetrics(allTrajectories);
        if (_costEvaluator != null && allTrajectories.Count > 0)
        {
            var batchEval = _costEvaluator.EvaluateBatch(allTrajectories);
            foreach (var kv in batchEval)
                metrics[kv.Key] = kv.Value;
        }

        return new GRPOTrainingResult(
            Math.Round(finalAvg, 3),
            Math.Round(bestReward, 3),
            0,
            0,
            allTrajectories.Count,
            allTrajectories.Sum(t => t.StepCount),
            sw.ElapsedMilliseconds,
            metrics);
    }

    public async Task<GRPOTrainingResult> TrainAsyncPartial(
        IReadOnlyList<string> tasks,
        RolloutConfig? config = null,
        int partialSteps = 5,
        CancellationToken cancellationToken = default)
    {
        var cfg = (config ?? new RolloutConfig()) with
        {
            EnablePartialRollout = true,
            PartialRolloutSteps = partialSteps
        };

        var sw = Stopwatch.StartNew();
        var allTrajectories = new List<InteractionTrajectory>();
        double bestReward = 0;

        var queuedTasks = new Queue<string>(tasks);
        var activeTrajectories = new Dictionary<string, InteractionTrajectory>();

        while (queuedTasks.Count > 0 || activeTrajectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchTasks = new List<string>();
            for (int i = 0; i < Math.Min(cfg.MaxConcurrent, queuedTasks.Count); i++)
                batchTasks.Add(queuedTasks.Dequeue());

            if (batchTasks.Count > 0)
            {
                await foreach (var traj in _interactionLoop.RunBatchAsync(batchTasks, cfg, cancellationToken))
                {
                    if (traj.Completed)
                    {
                        allTrajectories.Add(traj);
                        UpdateToolPreferences(traj);
                        UpdateStepRewardStats(traj);

                        if (traj.TotalReward > bestReward) bestReward = traj.TotalReward;

                        if (traj.SessionId != null)
                            activeTrajectories.Remove(traj.SessionId);
                    }
                    else if (traj.SessionId != null)
                    {
                        activeTrajectories[traj.SessionId] = traj;
                    }
                }
            }

            foreach (var (sessionId, partialTraj) in activeTrajectories.ToList())
            {
                var restored = _interactionLoop.RestoreFromCheckpoint(partialTraj.TrajectoryId);
                if (restored != null && restored.Completed)
                {
                    allTrajectories.Add(restored);
                    activeTrajectories.Remove(sessionId);
                }
            }
        }

        if (allTrajectories.Count > 0)
        {
            var advantage = ComputeGroupAdvantage(allTrajectories);
            ApplyPolicyGradient(allTrajectories, advantage);
            _traceReward?.TightenReference(allTrajectories);
        }

        StoreTrajectories(allTrajectories);
        sw.Stop();

        var finalAvg = allTrajectories.Count > 0
            ? allTrajectories.Average(t => t.TotalReward)
            : 0;

        var metricsPartial = GetMetrics(allTrajectories);
        if (_costEvaluator != null && allTrajectories.Count > 0)
        {
            var batchEval = _costEvaluator.EvaluateBatch(allTrajectories);
            foreach (var kv in batchEval)
                metricsPartial[kv.Key] = kv.Value;
        }

        return new GRPOTrainingResult(
            Math.Round(finalAvg, 3),
            Math.Round(bestReward, 3),
            0,
            0,
            allTrajectories.Count,
            allTrajectories.Sum(t => t.StepCount),
            sw.ElapsedMilliseconds,
            metricsPartial);
    }

    public Dictionary<string, double> GetToolPreferences() { lock (_lock) return new(_toolPreferences); }

    public Dictionary<string, (double mean, double std)> GetStepRewardStats() { lock (_lock) return new(_stepRewardStats); }

    public List<InteractionTrajectory> GetTrajectoryHistory(int? lastN = null)
    {
        lock (_lock)
        {
            return lastN.HasValue
                ? _trajectoryHistory.TakeLast(lastN.Value).ToList()
                : _trajectoryHistory.ToList();
        }
    }

    private Dictionary<string, double> ComputeGroupAdvantage(List<InteractionTrajectory> trajectories)
    {
        if (trajectories.Count == 0)
            return new();

        var rewards = trajectories.Select(t =>
            _traceReward?.ComputeCostNormalizedReward(t) ?? t.TotalReward).ToList();

        var mean = rewards.Average();
        var std = Math.Sqrt(rewards.Average(r => (r - mean) * (r - mean)) + 1e-8);

        var advantages = new Dictionary<string, double>();
        foreach (var traj in trajectories)
        {
            var normalized = (rewards[trajectories.IndexOf(traj)] - mean) / std;
            var clipped = Math.Clamp(normalized, -2.0, 2.0);
            advantages[traj.TrajectoryId] = Math.Round(clipped, 3);
        }

        return advantages;
    }

    private void ApplyPolicyGradient(
        List<InteractionTrajectory> trajectories,
        Dictionary<string, double> advantages)
    {
        foreach (var traj in trajectories)
        {
            if (!advantages.TryGetValue(traj.TrajectoryId, out var advantage))
                continue;

            foreach (var step in traj.Steps)
            {
                if (step.Action == null) continue;

                var toolName = step.Action.ToolName;
                double currentPref;
                lock (_lock) { currentPref = _toolPreferences.GetValueOrDefault(toolName, 0.5); }

                var gradient = advantage * step.Reward * LearningRate;
                var newPref = Math.Clamp(currentPref + gradient, 0.01, 0.99);

                lock (_lock) { _toolPreferences[toolName] = newPref; }
            }
        }
    }

    private void UpdateToolPreferences(InteractionTrajectory trajectory)
    {
        lock (_lock)
        {
            foreach (var step in trajectory.Steps)
            {
                if (step.Action == null) continue;

                var toolName = step.Action.ToolName;
                var currentPref = _toolPreferences.GetValueOrDefault(toolName, 0.5);
                var update = step.Reward > 0.5 ? 0.02 : -0.01;
                _toolPreferences[toolName] = Math.Clamp(currentPref + update, 0.01, 0.99);
            }
        }
    }

    private void UpdateStepRewardStats(InteractionTrajectory trajectory)
    {
        lock (_lock)
        {
            foreach (var step in trajectory.Steps)
            {
                var key = $"step_{step.StepIndex}";
                if (_stepRewardStats.TryGetValue(key, out var stats))
                {
                    var n = trajectory.Steps.Count;
                    var newMean = stats.mean * 0.9 + step.Reward * 0.1;
                    var newStd = Math.Sqrt(stats.std * stats.std * 0.9 + (step.Reward - newMean) * (step.Reward - newMean) * 0.1);
                    _stepRewardStats[key] = (newMean, newStd);
                }
                else
                {
                    _stepRewardStats[key] = (step.Reward, 0.1);
                }
            }
        }
    }

    private void StoreTrajectories(List<InteractionTrajectory> trajectories)
    {
        lock (_lock)
        {
            _trajectoryHistory.AddRange(trajectories);

            while (_trajectoryHistory.Count > MaxTrajectoryHistory)
                _trajectoryHistory.RemoveAt(0);
        }
    }

    private static Dictionary<string, object> GetMetrics(List<InteractionTrajectory> trajectories)
    {
        if (trajectories.Count == 0)
            return new() { ["total"] = 0 };

        return new()
        {
            ["total_trajectories"] = trajectories.Count,
            ["completed"] = trajectories.Count(t => t.Completed),
            ["avg_steps"] = Math.Round(trajectories.Average(t => t.StepCount), 2),
            ["avg_reward"] = Math.Round(trajectories.Average(t => t.TotalReward), 3),
            ["best_reward"] = Math.Round(trajectories.Max(t => t.TotalReward), 3),
            ["used_tools"] = trajectories
                .SelectMany(t => t.Steps)
                .Select(s => s.Action?.ToolName)
                .Where(n => n != null)
                .Distinct()
                .Cast<string>()
                .ToList()
        };
    }
}
