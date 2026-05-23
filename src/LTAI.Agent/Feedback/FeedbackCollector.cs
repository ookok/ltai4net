using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Feedback;

public enum FeedbackSentiment { Positive, Negative, Neutral }

public sealed record FeedbackEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AgentName { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string UserQuery { get; init; } = "";
    public string AgentResponse { get; init; } = "";
    public FeedbackSentiment Sentiment { get; init; }
    public string? Comment { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class AgentQualityScore
{
    public string AgentName { get; init; } = "";
    public int TotalFeedback { get; init; }
    public int PositiveCount { get; init; }
    public int NegativeCount { get; init; }
    public int NeutralCount { get; init; }
    public double PositiveRate { get; init; }
    public double QualityScore { get; init; }
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}

public sealed class FeedbackCollector
{
    private readonly ILogger<FeedbackCollector> _logger;
    private readonly ConcurrentDictionary<string, List<FeedbackEntry>> _agentFeedback = new();
    private readonly object _lock = new();

    public FeedbackCollector(ILogger<FeedbackCollector> logger)
    {
        _logger = logger;
    }

    public void RecordFeedback(FeedbackEntry entry)
    {
        lock (_lock)
        {
            var list = _agentFeedback.GetOrAdd(entry.AgentName, _ => new List<FeedbackEntry>());
            list.Add(entry);

            // Keep only last 1000 feedback entries per agent
            if (list.Count > 1000)
                list.RemoveRange(0, list.Count - 1000);
        }

        _logger.LogInformation("Feedback recorded: agent={Agent} sentiment={Sentiment} comment={Comment}",
            entry.AgentName, entry.Sentiment, entry.Comment ?? "(none)");
    }

    public AgentQualityScore GetQualityScore(string agentName)
    {
        lock (_lock)
        {
            if (!_agentFeedback.TryGetValue(agentName, out var list) || list.Count == 0)
            {
                return new AgentQualityScore
                {
                    AgentName = agentName,
                    QualityScore = 0.5 // Default neutral score
                };
            }

            var positive = list.Count(f => f.Sentiment == FeedbackSentiment.Positive);
            var negative = list.Count(f => f.Sentiment == FeedbackSentiment.Negative);
            var neutral = list.Count(f => f.Sentiment == FeedbackSentiment.Neutral);

            // Quality score: weighted average (positive=1.0, neutral=0.5, negative=0.0)
            var qualityScore = (positive * 1.0 + neutral * 0.5 + negative * 0.0) / list.Count;

            return new AgentQualityScore
            {
                AgentName = agentName,
                TotalFeedback = list.Count,
                PositiveCount = positive,
                NegativeCount = negative,
                NeutralCount = neutral,
                PositiveRate = (double)positive / list.Count,
                QualityScore = qualityScore,
                LastUpdated = list.Max(f => f.Timestamp)
            };
        }
    }

    public Dictionary<string, AgentQualityScore> GetAllQualityScores()
    {
        lock (_lock)
        {
            return _agentFeedback.Keys.ToDictionary(
                agentName => agentName,
                agentName => GetQualityScore(agentName));
        }
    }

    public List<FeedbackEntry> GetRecentFeedback(string agentName, int count = 10)
    {
        lock (_lock)
        {
            if (!_agentFeedback.TryGetValue(agentName, out var list))
                return new List<FeedbackEntry>();

            return list.OrderByDescending(f => f.Timestamp).Take(count).ToList();
        }
    }
}
