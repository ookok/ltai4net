using System.Collections.Concurrent;

namespace LTAI.Network.Links;

public sealed record ReputationScore
{
    public string PeerId { get; init; } = string.Empty;
    public double Score { get; init; }
    public DateTime LastUpdate { get; init; } = DateTime.UtcNow;
    public string? BanReason { get; init; }
}

public sealed class Reputation
{
    private static readonly Lazy<Reputation> _instance = new(() => new Reputation());
    public static Reputation Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ReputationScore> _scores = new();
    private readonly ConcurrentDictionary<string, DateTime> _banned = new();
    private const double DecayInterval = 10.0;
    private const double MinScore = -10.0;
    private const double MaxScore = 10.0;

    private Reputation()
    {
    }

    public void RatePeer(string peerId, double delta)
    {
        _scores.AddOrUpdate(
            peerId,
            _ => new ReputationScore
            {
                PeerId = peerId,
                Score = Math.Clamp(delta, MinScore, MaxScore),
                LastUpdate = DateTime.UtcNow
            },
            (_, existing) => existing with
            {
                Score = Math.Clamp(existing.Score + delta, MinScore, MaxScore),
                LastUpdate = DateTime.UtcNow
            });
    }

    public double GetScore(string peerId)
    {
        if (!_scores.TryGetValue(peerId, out var score))
            return 0.0;

        var age = (DateTime.UtcNow - score.LastUpdate).TotalSeconds;
        var decayFactor = Math.Pow(0.95, age / DecayInterval);
        return Math.Clamp(score.Score * decayFactor, MinScore, MaxScore);
    }

    public bool IsTrusted(string peerId, double threshold = 1.0)
    {
        if (IsBanned(peerId))
            return false;

        return GetScore(peerId) >= threshold;
    }

    public void BanNode(string peerId, string reason, double durationSeconds)
    {
        _banned[peerId] = DateTime.UtcNow.AddSeconds(durationSeconds);
    }

    public bool IsBanned(string peerId)
    {
        if (!_banned.TryGetValue(peerId, out var expiry))
            return false;

        if (DateTime.UtcNow >= expiry)
        {
            _banned.TryRemove(peerId, out _);
            return false;
        }

        return true;
    }

    public void DecayScores()
    {
        foreach (var peerId in _scores.Keys)
        {
            _scores.TryGetValue(peerId, out var score);
            if (score is not null)
            {
                var age = (DateTime.UtcNow - score.LastUpdate).TotalSeconds;
                var decayFactor = Math.Pow(0.95, age / DecayInterval);
                var decayed = score.Score * decayFactor;

                if (Math.Abs(decayed) < 0.01)
                    _scores.TryRemove(peerId, out _);
                else
                    _scores.TryUpdate(peerId, score with { Score = decayed }, score);
            }
        }
    }

    public IReadOnlyList<ReputationScore> GetAllScores()
    {
        var now = DateTime.UtcNow;
        return _scores.Values.Select(s =>
        {
            var age = (now - s.LastUpdate).TotalSeconds;
            var decayFactor = Math.Pow(0.95, age / DecayInterval);
            return s with { Score = Math.Clamp(s.Score * decayFactor, MinScore, MaxScore) };
        }).ToList();
    }

    public (int PeerCount, int BannedCount, double AvgScore) Stats()
    {
        var scores = GetAllScores();
        return (
            _scores.Count,
            _banned.Count,
            scores.Count > 0 ? scores.Average(s => s.Score) : 0.0
        );
    }
}
