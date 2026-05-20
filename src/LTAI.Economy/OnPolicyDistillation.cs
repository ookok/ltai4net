using System.Collections.Concurrent;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed record OPDRecord
{
    public string TrajectoryId { get; init; } = "";
    public string FailedStep { get; init; } = "";
    public List<string> TeacherTokens { get; init; } = new();
    public List<double> TeacherLogProbs { get; init; } = new();
    public List<string> StudentTokens { get; init; } = new();
    public List<double> StudentLogProbs { get; init; } = new();
    public double KLDivergence { get; init; }
    public double DistillationLoss { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class OnPolicyDistillation
{
    private readonly List<OPDRecord> _history = new();
    private readonly Dictionary<string, double> _tokenCorrectionWeights = new();
    private readonly Dictionary<string, int> _tokenCorrectionCounts = new();
    private const int MaxHistory = 200;
    private const double DistillationAlpha = 0.05;
    private readonly object _lock = new();
    private readonly ILogger<OnPolicyDistillation>? _logger;

    public OnPolicyDistillation(ILogger<OnPolicyDistillation>? logger = null)
    {
        _logger = logger;
    }

    public OPDRecord? DistillFailedRollout(
        InteractionTrajectory failedTrajectory,
        IReadOnlyList<(string token, double logProb)> teacherDenseSignals,
        IReadOnlyList<(string token, double logProb)> studentTokens,
        int failedStepIdx)
    {
        if (teacherDenseSignals.Count == 0 || studentTokens.Count == 0)
            return null;

        var clippedTeacher = teacherDenseSignals.Take(100).ToList();
        var clippedStudent = studentTokens.Take(100).ToList();

        double klSum = 0;
        int count = Math.Min(clippedTeacher.Count, clippedStudent.Count);

        for (int i = 0; i < count; i++)
        {
            var teacherp = Math.Max(clippedTeacher[i].logProb, -50);
            var studentp = Math.Max(clippedStudent[i].logProb, -50);
            var diff = teacherp - studentp;
            klSum += Math.Abs(diff);
        }

        var klDivergence = count > 0 ? klSum / count : 0;
        var distillationLoss = klDivergence * (1.0 - failedTrajectory.TotalReward);

        var record = new OPDRecord
        {
            TrajectoryId = failedTrajectory.TrajectoryId,
            FailedStep = $"step_{failedStepIdx}",
            TeacherTokens = clippedTeacher.Select(t => t.token).ToList(),
            TeacherLogProbs = clippedTeacher.Select(t => t.logProb).ToList(),
            StudentTokens = clippedStudent.Select(t => t.token).ToList(),
            StudentLogProbs = clippedStudent.Select(t => t.logProb).ToList(),
            KLDivergence = Math.Round(klDivergence, 4),
            DistillationLoss = Math.Round(distillationLoss, 4)
        };

        lock (_lock)
        {
            _history.Add(record);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);

            for (int i = 0; i < count; i++)
            {
                var token = clippedTeacher[i].token;
                var correctionSignal = clippedTeacher[i].logProb - clippedStudent[i].logProb;

                var currentWeight = _tokenCorrectionWeights.GetValueOrDefault(token, 0);
                var correctionCount = _tokenCorrectionCounts.GetValueOrDefault(token, 0);

                _tokenCorrectionWeights[token] = currentWeight * 0.9 + correctionSignal * 0.1;
                _tokenCorrectionCounts[token] = correctionCount + 1;
            }
        }

        _logger?.LogInformation(
            "OPD: trajectory={TrajId} step={Step} klDiv={KL:F4} loss={Loss:F4}",
            failedTrajectory.TrajectoryId, failedStepIdx, klDivergence, distillationLoss);

        return record;
    }

    public double ComputeDistillationBonus(
        InteractionTrajectory trajectory,
        IReadOnlyList<(string token, double logProb)> currentTokens)
    {
        if (currentTokens.Count == 0) return 0;

        double totalCorrection = 0;
        int matchCount = 0;

        foreach (var (token, _) in currentTokens)
        {
            lock (_lock)
            {
                if (_tokenCorrectionWeights.TryGetValue(token, out var weight))
                {
                    totalCorrection += weight;
                    matchCount++;
                }
            }
        }

        if (matchCount == 0) return 0;

        var avgCorrection = totalCorrection / matchCount;
        return Math.Clamp(avgCorrection * DistillationAlpha, -0.1, 0.1);
    }

    public Dictionary<string, object> GetTopCorrections(int topN = 10)
    {
        lock (_lock)
        {
            return new()
            {
                ["top_corrections"] = _tokenCorrectionWeights
                    .Where(kv => Math.Abs(kv.Value) > 0.01)
                    .OrderByDescending(kv => Math.Abs(kv.Value))
                    .Take(topN)
                    .Select(kv => new { token = kv.Key, weight = Math.Round(kv.Value, 4) })
                    .ToList(),
                ["total_corrections"] = _tokenCorrectionWeights.Count
            };
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var recent = _history.TakeLast(50).ToList();
            return new()
            {
                ["total_opd_records"] = _history.Count,
                ["avg_kl_divergence"] = Math.Round(recent.Count > 0 ? recent.Average(r => r.KLDivergence) : 0, 4),
                ["avg_distillation_loss"] = Math.Round(recent.Count > 0 ? recent.Average(r => r.DistillationLoss) : 0, 4),
                ["corrected_tokens"] = _tokenCorrectionWeights.Count,
                ["active_corrections"] = _tokenCorrectionWeights.Count(kv => Math.Abs(kv.Value) > 0.005)
            };
        }
    }

    public List<OPDRecord> GetRecentRecords(int count = 10)
    {
        lock (_lock) { return _history.TakeLast(count).Reverse().ToList(); }
    }
}
