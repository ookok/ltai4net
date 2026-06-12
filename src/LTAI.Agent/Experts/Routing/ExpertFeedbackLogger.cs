using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Records expert routing decisions and their outcomes to enable
/// feedback-driven improvement of the ExpertRouter over time.
///
/// Each entry captures: query text, selected experts with router confidence,
/// per-expert response confidence, NoAnswer flags, and whether the aggregated
/// context was useful. This data feeds into future routing adjustments.
/// </summary>
public sealed class ExpertFeedbackLogger
{
    private readonly ConcurrentQueue<ExpertFeedbackEntry> _entries = new();
    private readonly ILogger<ExpertFeedbackLogger>? _logger;
    private const int MaxEntries = 200;

    public ExpertFeedbackLogger(ILogger<ExpertFeedbackLogger>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Record a routing decision and its outcomes.
    /// </summary>
    public void Record(
        string query,
        ExpertSelectionResult selection,
        IReadOnlyList<ExpertResponse> responses,
        AggregatedContext aggregated)
    {
        var entry = new ExpertFeedbackEntry(
            DateTime.UtcNow,
            query,
            selection.Selections.Select(s => (s.ExpertId, s.Confidence)).ToList(),
            responses.Where(r => !r.NoAnswer).Select(r => (r.ExpertId, r.Confidence)).ToList(),
            responses.Count(r => r.NoAnswer),
            aggregated.HasAnswer,
            aggregated.AggregateConfidence);

        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        var truncated = query.Length > 60 ? query[..60] : query;
        _logger?.LogDebug("ExpertFeedback: query='{Query}' | {Answered}/{Total} experts answered | aggregate confidence {Conf:P0}",
            truncated, entry.AnsweredCount, entry.SelectedCount, entry.AggregateConfidence);
    }

    /// <summary>
    /// Get per-expert success statistics from recent history.
    /// Returns expert IDs with their answer rate and average confidence.
    /// </summary>
    public IReadOnlyDictionary<string, ExpertFeedbackStat> GetStats()
    {
        var entries = _entries.ToArray();
        var stats = new Dictionary<string, ExpertFeedbackStat>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            foreach (var (expertId, routerConf) in entry.SelectedExperts)
            {
                if (!stats.TryGetValue(expertId, out var stat))
                {
                    stat = new ExpertFeedbackStat(expertId);
                    stats[expertId] = stat;
                }
                stat.SelectionCount++;
                stat.TotalRouterConfidence += routerConf;
            }

            foreach (var (expertId, responseConf) in entry.AnsweredExperts)
            {
                if (stats.TryGetValue(expertId, out var stat))
                {
                    stat.AnswerCount++;
                    stat.TotalResponseConfidence += responseConf;
                }
            }

            foreach (var (expertId, _) in entry.SelectedExperts)
            {
                if (stats.TryGetValue(expertId, out var stat) && entry.AggregateHasAnswer)
                    stat.SuccessfulQueryCount++;
            }
        }

        return stats;
    }

    /// <summary>
    /// Get recent entries for analysis (most recent first).
    /// </summary>
    public IReadOnlyList<ExpertFeedbackEntry> GetRecentEntries(int count = 20)
    {
        return _entries.ToArray().Reverse().Take(count).ToList();
    }

    public int EntryCount => _entries.Count;
}
