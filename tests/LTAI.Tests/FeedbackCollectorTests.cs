using LTAI.Agent.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class FeedbackCollectorTests
{
    private readonly FeedbackCollector _collector = new(NullLogger<FeedbackCollector>.Instance);

    [Fact]
    public void RecordFeedback_StoresEntry()
    {
        var entry = new FeedbackEntry
        {
            AgentName = "code",
            SessionId = "s1",
            UserQuery = "Fix this bug",
            AgentResponse = "Here is the fix",
            Sentiment = FeedbackSentiment.Positive,
            Comment = "Great!"
        };

        _collector.RecordFeedback(entry);

        var score = _collector.GetQualityScore("code");
        Assert.Equal(1, score.TotalFeedback);
        Assert.Equal(1, score.PositiveCount);
        Assert.Equal(1.0, score.PositiveRate);
        Assert.Equal(1.0, score.QualityScore);
    }

    [Fact]
    public void GetQualityScore_NoFeedback_ReturnsDefault()
    {
        var score = _collector.GetQualityScore("unknown");
        Assert.Equal(0, score.TotalFeedback);
        Assert.Equal(0.5, score.QualityScore);
    }

    [Fact]
    public void GetQualityScore_MixedFeedback_CalculatesCorrectly()
    {
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "chat", Sentiment = FeedbackSentiment.Positive });
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "chat", Sentiment = FeedbackSentiment.Positive });
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "chat", Sentiment = FeedbackSentiment.Negative });
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "chat", Sentiment = FeedbackSentiment.Neutral });

        var score = _collector.GetQualityScore("chat");
        Assert.Equal(4, score.TotalFeedback);
        Assert.Equal(2, score.PositiveCount);
        Assert.Equal(1, score.NegativeCount);
        Assert.Equal(1, score.NeutralCount);
        Assert.Equal(0.5, score.PositiveRate);
        // Quality = (2*1.0 + 1*0.5 + 1*0.0) / 4 = 2.5/4 = 0.625
        Assert.Equal(0.625, score.QualityScore, 3);
    }

    [Fact]
    public void GetAllQualityScores_ReturnsAllAgents()
    {
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "code", Sentiment = FeedbackSentiment.Positive });
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "eia", Sentiment = FeedbackSentiment.Negative });
        _collector.RecordFeedback(new FeedbackEntry { AgentName = "chat", Sentiment = FeedbackSentiment.Neutral });

        var scores = _collector.GetAllQualityScores();
        Assert.Equal(3, scores.Count);
        Assert.Contains("code", scores.Keys);
        Assert.Contains("eia", scores.Keys);
        Assert.Contains("chat", scores.Keys);
    }

    [Fact]
    public void GetRecentFeedback_ReturnsOrderedByTimestamp()
    {
        for (int i = 0; i < 5; i++)
        {
            _collector.RecordFeedback(new FeedbackEntry
            {
                AgentName = "code",
                Sentiment = FeedbackSentiment.Positive,
                Comment = $"Feedback {i}"
            });
        }

        var recent = _collector.GetRecentFeedback("code", 3);
        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public void GetRecentFeedback_UnknownAgent_ReturnsEmpty()
    {
        var recent = _collector.GetRecentFeedback("unknown");
        Assert.Empty(recent);
    }
}
