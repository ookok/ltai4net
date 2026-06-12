namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// A single routing feedback entry recording one query cycle.
/// </summary>
public sealed record ExpertFeedbackEntry(
    DateTime Timestamp,
    string Query,
    IReadOnlyList<(string ExpertId, float RouterConfidence)> SelectedExperts,
    IReadOnlyList<(string ExpertId, float ResponseConfidence)> AnsweredExperts,
    int NoAnswerCount,
    bool AggregateHasAnswer,
    float AggregateConfidence)
{
    public int SelectedCount => SelectedExperts.Count;
    public int AnsweredCount => AnsweredExperts.Count;
}

/// <summary>
/// Aggregated per-expert statistics from feedback history.
/// </summary>
public sealed class ExpertFeedbackStat
{
    public string ExpertId { get; }

    /// <summary>How many times this expert was selected by the Router.</summary>
    public int SelectionCount;

    /// <summary>How many times this expert returned an answer (not NoAnswer).</summary>
    public int AnswerCount;

    /// <summary>How many queries where this expert participated resulted in a useful aggregate answer.</summary>
    public int SuccessfulQueryCount;

    /// <summary>Cumulative router-assigned confidence (for averaging).</summary>
    public float TotalRouterConfidence;

    /// <summary>Cumulative response confidence (for averaging).</summary>
    public float TotalResponseConfidence;

    public ExpertFeedbackStat(string expertId)
    {
        ExpertId = expertId;
    }

    public double AnswerRate => SelectionCount > 0 ? (double)AnswerCount / SelectionCount : 0.5;
    public double SuccessRate => SelectionCount > 0 ? (double)SuccessfulQueryCount / SelectionCount : 0.5;
    public double AvgRouterConfidence => SelectionCount > 0 ? (double)TotalRouterConfidence / SelectionCount : 0;
    public double AvgResponseConfidence => AnswerCount > 0 ? (double)TotalResponseConfidence / AnswerCount : 0;

    /// <summary>
    /// Recommended confidence boost for routing decisions.
    /// Positive = boost this expert's priority; negative = demote.
    /// Range: [-0.2, +0.2].
    /// </summary>
    public double ConfidenceBoost
    {
        get
        {
            if (SelectionCount < 3) return 0; // Not enough data
            double score = (AnswerRate - 0.5) * 0.4;  // ±0.2 from answer rate
            return Math.Clamp(score, -0.2, 0.2);
        }
    }
}
