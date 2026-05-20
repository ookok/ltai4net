using LTAI.Core.System;

namespace LTAI.TreeLLM.Session;

public sealed record DisclosurePolicyConfig
{
    public double EntailmentThresholdForSpeak { get; init; } = 0.6;
    public int MinSpeakBlockTokens { get; init; } = 20;
    public double LatencyWeight { get; init; } = 0.1;
    public int MaxTrainingRounds { get; init; } = 100;
    public double LearningRate { get; init; } = 0.01;
    public int GroupSize { get; init; } = 16;
}

public sealed record DisclosurePolicyStats
{
    public int TotalSteps { get; init; }
    public int SpeakSteps { get; init; }
    public int ThinkSteps { get; init; }
    public double AvgEntailmentAtSpeak { get; init; }
    public double AvgAirw { get; init; }
    public double DisclosureRatio { get; init; }
    public double PolicyLoss { get; init; }
    public int TrainingRounds { get; init; }
}

public sealed class DisclosurePolicyLearner
{
    private readonly EntailmentAligner _aligner;
    private readonly DisclosurePolicyConfig _config;
    private readonly Dictionary<int, double> _stepDisclosureWeights = new();
    private readonly List<DisclosurePolicyStats> _trainingHistory = new();
    private int _trainingRounds;

    public DisclosurePolicyLearner(
        EntailmentAligner aligner,
        DisclosurePolicyConfig? config = null)
    {
        _aligner = aligner;
        _config = config ?? new();
    }

    public List<AgentStep> ApplyPolicyToTrajectory(InteractionTrajectory trajectory)
    {
        var decisions = _aligner.ComputeDisclosureDecisions(trajectory);
        var result = new List<AgentStep>();

        for (int i = 0; i < trajectory.Steps.Count; i++)
        {
            var step = trajectory.Steps[i];
            var decision = decisions.FirstOrDefault(d => d.StepIndex == i);

            var disclosure = decision?.Action ?? DisclosureAction.Think;

            var weightedScore = _stepDisclosureWeights.TryGetValue(i, out var w)
                ? decision?.EntailmentScore * w ?? 0
                : decision?.EntailmentScore ?? 0;

            result.Add(step with
            {
                Disclosure = weightedScore >= _config.EntailmentThresholdForSpeak
                    ? DisclosureAction.Speak
                    : DisclosureAction.Think
            });
        }

        return result;
    }

    public DisclosurePolicyStats RunSftTraining(List<InteractionTrajectory> trajectories)
    {
        int totalSteps = 0, speakSteps = 0, thinkSteps = 0;
        double totalEntailment = 0;
        int speakCount = 0;

        foreach (var traj in trajectories)
        {
            var aligned = _aligner.BuildInterleavedTrajectory(traj);

            var speakFrags = aligned.Where(a => a.Action == DisclosureAction.Speak).ToList();

            foreach (var frag in speakFrags)
            {
                totalEntailment += frag.EntailmentScore;
                speakCount++;
            }

            totalSteps += aligned.Count;
            speakSteps += aligned.Count(a => a.Action == DisclosureAction.Speak);
            thinkSteps += aligned.Count(a => a.Action == DisclosureAction.Think);
        }

        for (int i = 0; i < Math.Min(30, totalSteps); i++)
        {
            _stepDisclosureWeights[i] = 1.0;
        }

        var stats = new DisclosurePolicyStats
        {
            TotalSteps = totalSteps,
            SpeakSteps = speakSteps,
            ThinkSteps = thinkSteps,
            AvgEntailmentAtSpeak = speakCount > 0 ? totalEntailment / speakCount : 0,
            AvgAirw = thinkSteps > 0 ? (double)thinkSteps / Math.Max(1, speakSteps) : 0,
            DisclosureRatio = totalSteps > 0 ? (double)speakSteps / totalSteps : 0,
            PolicyLoss = 0,
            TrainingRounds = 0
        };

        _trainingHistory.Add(stats);
        return stats;
    }

    public DisclosurePolicyStats RunRlTraining(List<InteractionTrajectory> trajectories)
    {
        var groups = trajectories
            .Select((t, i) => (traj: t, idx: i))
            .GroupBy(x => x.idx / _config.GroupSize)
            .Select(g => g.Select(x => x.traj).ToList())
            .ToList();

        double totalPolicyLoss = 0;

        for (int round = 0; round < _config.MaxTrainingRounds && round < groups.Count; round++)
        {
            var group = groups[round];
            var groupStats = ComputeGroupStats(group);

            var policyGrad = ComputePolicyGradient(group, groupStats);
            totalPolicyLoss += policyGrad;

            ApplyWeightUpdate(group, groupStats);

            _trainingRounds++;

            if (Math.Abs(policyGrad) < 0.001)
                break;
        }

        int totalSteps = 0, speakSteps = 0, thinkSteps = 0;
        double totalEntailment = 0;
        int speakCount = 0;

        foreach (var traj in trajectories)
        {
            var latency = traj.LatencySnapshot;
            totalSteps += traj.StepCount;
            speakSteps += latency.SpeakTokenCount > 0 ? traj.StepCount : 0;
            thinkSteps += latency.ThinkTokenCount > 0 ? traj.StepCount : 0;

            var decisions = _aligner.ComputeDisclosureDecisions(traj);
            foreach (var d in decisions.Where(d => d.IsDisclosed))
            {
                totalEntailment += d.EntailmentScore;
                speakCount++;
            }
        }

        var finalStats = new DisclosurePolicyStats
        {
            TotalSteps = totalSteps,
            SpeakSteps = speakSteps,
            ThinkSteps = thinkSteps,
            AvgEntailmentAtSpeak = speakCount > 0 ? totalEntailment / speakCount : 0,
            AvgAirw = speakSteps > 0 ? (double)thinkSteps / Math.Max(1, speakSteps) : 0,
            DisclosureRatio = totalSteps > 0 ? (double)speakSteps / totalSteps : 0,
            PolicyLoss = Math.Round(totalPolicyLoss / Math.Max(1, _trainingRounds), 4),
            TrainingRounds = _trainingRounds
        };

        _trainingHistory.Add(finalStats);
        return finalStats;
    }

    public DisclosurePolicyStats RunFullTraining(List<InteractionTrajectory> trajectories)
    {
        var sftStats = RunSftTraining(trajectories);
        var rlStats = RunRlTraining(trajectories);
        return rlStats;
    }

    public List<DisclosurePolicyStats> GetTrainingHistory() => _trainingHistory;

    public DisclosurePolicyStats GetCurrentStats()
    {
        return new DisclosurePolicyStats
        {
            TrainingRounds = _trainingRounds,
            PolicyLoss = _trainingHistory.LastOrDefault()?.PolicyLoss ?? 0
        };
    }

    private (double avgReward, double avgAirw, double groupStd) ComputeGroupStats(
        List<InteractionTrajectory> group)
    {
        double totalReward = 0, totalAirw = 0;
        int validReward = 0, validAirw = 0;

        foreach (var traj in group)
        {
            totalReward += traj.TotalReward;
            validReward++;

            var latency = traj.LatencySnapshot;
            if (latency.AIRW > 0)
            {
                totalAirw += latency.AIRW;
                validAirw++;
            }
        }

        double avgR = validReward > 0 ? totalReward / validReward : 0;
        double avgA = validAirw > 0 ? totalAirw / validAirw : 0;

        double sumSq = 0;
        foreach (var traj in group)
        {
            sumSq += Math.Pow(traj.TotalReward - avgR, 2);
        }
        double std = Math.Sqrt(sumSq / Math.Max(1, group.Count));

        return (avgR, avgA, std);
    }

    private double ComputePolicyGradient(
        List<InteractionTrajectory> group,
        (double avgReward, double avgAirw, double groupStd) stats)
    {
        double gradient = 0;

        if (stats.groupStd < 0.01)
            return 0;

        foreach (var traj in group)
        {
            var latency = traj.LatencySnapshot;
            var advantage = (traj.TotalReward - stats.avgReward) / stats.groupStd;
            var latencyPenalty = _config.LatencyWeight * latency.AIRW;
            gradient += advantage - latencyPenalty;
        }

        return gradient / group.Count;
    }

    private void ApplyWeightUpdate(
        List<InteractionTrajectory> group,
        (double avgReward, double avgAirw, double groupStd) stats)
    {
        if (stats.groupStd < 0.01)
            return;

        for (int i = 0; i < group.Count; i++)
        {
            var traj = group[i];
            var advantage = (traj.TotalReward - stats.avgReward) / stats.groupStd;

            for (int s = 0; s < traj.StepCount; s++)
            {
                var existingWeight = _stepDisclosureWeights.TryGetValue(s, out var w) ? w : 1.0;
                var update = advantage * _config.LearningRate;
                _stepDisclosureWeights[s] = Math.Clamp(existingWeight + update, 0.3, 2.0);
            }
        }
    }
}
