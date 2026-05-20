using LTAI.Core.System;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed record ReplayTrajectory
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string TaskDescription { get; init; } = "";
    public InteractionTrajectory Trajectory { get; init; } = null!;
    public double SuccessScore { get; init; }
    public int ReplayCount { get; set; }
    public double LastReplayedAt { get; set; }
    public double StoredAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public List<string> KeyInsights { get; init; } = new();
    public string Domain { get; init; } = "general";
    public double ForgetFactor { get; set; }
}

public sealed class ExperienceReplayBuffer(ILogger<ExperienceReplayBuffer>? logger = null)
{
    private readonly Dictionary<string, ReplayTrajectory> _buffer = new();
    private readonly Dictionary<string, double> _domainStats = new();
    private readonly object _lock = new();
    private const int MaxBufferSize = 200;
    private const double MinReplayInterval = 60;
    private const double ForgetDecayLambda = 0.0005;
    private const double ForgetThreshold = 0.1;

    public void Store(InteractionTrajectory trajectory, double successScore,
        string? domain = null)
    {
        lock (_lock)
        {
            var id = $"replay_{trajectory.TrajectoryId}";
            var existId = id;

            var entry = new ReplayTrajectory
            {
                Id = existId,
                TaskDescription = trajectory.TaskDescription,
                Trajectory = trajectory,
                SuccessScore = successScore,
                ReplayCount = 0,
                KeyInsights = ExtractKeyInsights(trajectory),
                Domain = domain ?? "general"
            };

            _buffer[entry.Id] = entry;

            UpdateDomainStats(entry.Domain, successScore);

            if (_buffer.Count > MaxBufferSize)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var toRemove = _buffer.Values
                    .OrderBy(e => ComputeAdjustedScore(e, now))
                    .Take(_buffer.Count - MaxBufferSize)
                    .ToList();

                foreach (var item in toRemove)
                    _buffer.Remove(item.Id);
            }

            logger?.LogDebug(
                "ReplayBuffer: stored {Id} score={Score:F2} size={Size}",
                entry.Id, successScore, _buffer.Count);
        }
    }

    public List<ReplayTrajectory> Sample(
        int count, double replayRatio = 0.25,
        double explorationRatio = 0.15,
        string? domain = null)
    {
        lock (_lock)
        {
            var candidates = domain != null
                ? _buffer.Values.Where(e => e.Domain == domain).ToList()
                : _buffer.Values.ToList();

            if (candidates.Count == 0) return new();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var eligible = candidates
                .Where(e => now - e.LastReplayedAt >= MinReplayInterval)
                .ToList();

            if (eligible.Count == 0)
                eligible = candidates;

            var priorityCount = (int)(count * (1.0 - explorationRatio));
            var exploreCount = count - priorityCount;

            var prioritized = eligible
                .OrderByDescending(e => ComputeSamplePriority(e, now))
                .Take(priorityCount)
                .ToList();

            var rng = new Random();
            var explored = eligible
                .Except(prioritized)
                .OrderBy(_ => rng.NextDouble())
                .Take(exploreCount)
                .ToList();

            var sampled = prioritized.Concat(explored).ToList();
            foreach (var s in sampled) s.ReplayCount++;

            return sampled;
        }
    }

    public List<ReplayTrajectory> GetHighQualityTrajectories(
        double minScore = 0.7, int? limit = null)
    {
        lock (_lock)
        {
            var query = _buffer.Values
                .Where(e => e.SuccessScore >= minScore)
                .OrderByDescending(e => e.SuccessScore);

            return limit.HasValue ? query.Take(limit.Value).ToList() : query.ToList();
        }
    }

    public void UpdateReplayTime(string id)
    {
        lock (_lock)
        {
            if (_buffer.TryGetValue(id, out var entry))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                entry.LastReplayedAt = now;
                entry.ForgetFactor = ComputeForgetFactor(now - entry.StoredAt);
            }
        }
    }

    public int ApplyForgetDecay()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int removed = 0;
            var toRemove = new List<string>();

            foreach (var (id, entry) in _buffer)
            {
                var age = now - entry.StoredAt;
                entry.ForgetFactor = ComputeForgetFactor(age);

                if (entry.ForgetFactor < ForgetThreshold)
                    toRemove.Add(id);
            }

            foreach (var id in toRemove)
            {
                if (_buffer.Remove(id))
                    removed++;
            }

            logger?.LogDebug(
                "FadedPER: applied forget decay, removed={Removed} remaining={Remaining}",
                removed, _buffer.Count);

            return removed;
        }
    }

    public double GetForgetAdjustedPriority(string id)
    {
        lock (_lock)
        {
            if (_buffer.TryGetValue(id, out var entry))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return ComputeSamplePriority(entry, now);
            }
            return 0;
        }
    }

    private static double ComputeForgetFactor(double ageSeconds)
    {
        return Math.Exp(-ForgetDecayLambda * ageSeconds);
    }

    private static double ComputeAdjustedScore(ReplayTrajectory entry, double now)
    {
        var age = now - entry.StoredAt;
        var forgetFade = ComputeForgetFactor(age);
        return entry.SuccessScore * forgetFade
            * Math.Exp(-0.01 * entry.ReplayCount);
    }

    private static double ComputeSamplePriority(ReplayTrajectory entry, double now)
    {
        var age = now - entry.StoredAt;
        var forgetFade = ComputeForgetFactor(age);
        var tdAnalog = entry.SuccessScore * Math.Log(1 + entry.Trajectory.StepCount);
        return tdAnalog * forgetFade;
    }

    public int IngestToRag(AgenticRAG agenticRAG, double minScore = 0.85)
    {
        lock (_lock)
        {
            var highQuality = GetHighQualityTrajectories(minScore);
            int ingested = 0;

            foreach (var entry in highQuality)
            {
                try
                {
                    agenticRAG.Search(entry.TaskDescription, RAGMode.Iterative, domain: entry.Domain);
                    ingested++;
                }
                catch { }
            }

            return ingested;
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new()
            {
                ["buffer_size"] = _buffer.Count,
                ["avg_success_score"] = Math.Round(
                    _buffer.Values.Average(e => e.SuccessScore), 3),
                ["avg_replay_count"] = Math.Round(
                    _buffer.Values.Average(e => e.ReplayCount), 2),
                ["high_quality_count"] = _buffer.Values.Count(e => e.SuccessScore >= 0.7),
                ["avg_forget_factor"] = Math.Round(
                    _buffer.Values.Average(e => e.ForgetFactor), 3),
                ["forget_ratio"] = Math.Round(
                    (double)_buffer.Values.Count(e => e.ForgetFactor < ForgetThreshold) / Math.Max(1, _buffer.Count), 3),
                ["domains"] = _domainStats.Select(kv => new
                {
                    domain = kv.Key,
                    score = Math.Round(kv.Value, 3)
                }).ToList()
            };
        }
    }

    public void Clear(double olderThanMinutes = 1440)
    {
        lock (_lock)
        {
            var threshold = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - olderThanMinutes * 60;
            var toRemove = _buffer.Values
                .Where(e => e.StoredAt < threshold)
                .ToList();

            foreach (var item in toRemove)
                _buffer.Remove(item.Id);
        }
    }

    public int BufferSize { get { lock (_lock) return _buffer.Count; } }

    private static List<string> ExtractKeyInsights(InteractionTrajectory trajectory)
    {
        var insights = new List<string>();
        foreach (var step in trajectory.Steps)
        {
            if (step.Reward > 0.7)
            {
                insights.Add(step.Thought.Length > 120
                    ? step.Thought[..120]
                    : step.Thought);
            }
        }
        return insights.Take(10).ToList();
    }

    private void UpdateDomainStats(string domain, double score)
    {
        if (_domainStats.TryGetValue(domain, out var current))
            _domainStats[domain] = current * 0.9 + score * 0.1;
        else
            _domainStats[domain] = score;
    }
}
