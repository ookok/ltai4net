using System.Text;
using LTAI.Core.Execution;
using LTAI.Core.Models;
using LTAI.Planning.Metrics.Monitoring;

namespace LTAI.Agent.Evolution;

public sealed class FailurePattern
{
    public string Pattern { get; init; } = "";
    public string RootCause { get; init; } = "";
    public int OccurrenceCount { get; init; }
    public double AvgLatencyMs { get; init; }
    public string? SuggestedFix { get; init; }
    public string Severity { get; init; } = "medium";
}

public sealed class ExperienceReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public int TotalTraces { get; init; }
    public int ErrorCount { get; init; }
    public double ErrorRate { get; init; }
    public List<FailurePattern> Patterns { get; init; } = new();
    public Dictionary<string, int> ToolErrorCounts { get; init; } = new();
    public List<string> TopErrorMessages { get; init; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Experience Debugger Report");
        sb.AppendLine($"Generated: {GeneratedAt:O}");
        sb.AppendLine();
        sb.AppendLine($"**Traces analyzed:** {TotalTraces} | **Errors:** {ErrorCount} | **Error rate:** {ErrorRate:P1}");
        sb.AppendLine();

        if (Patterns.Count > 0)
        {
            sb.AppendLine("## Failure Patterns");
            sb.AppendLine("| # | Pattern | Root Cause | Occurrences | Severity | Suggested Fix |");
            sb.AppendLine("|---|---------|-----------|-------------|----------|---------------|");
            for (int i = 0; i < Patterns.Count; i++)
            {
                var p = Patterns[i];
                sb.AppendLine($"| {i + 1} | {p.Pattern} | {p.RootCause} | {p.OccurrenceCount} | {p.Severity} | {p.SuggestedFix ?? "-"} |");
            }
            sb.AppendLine();
        }

        if (ToolErrorCounts.Count > 0)
        {
            sb.AppendLine("## Tool Error Distribution");
            sb.AppendLine("| Tool | Errors |");
            sb.AppendLine("|------|--------|");
            foreach (var (tool, count) in ToolErrorCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"| {tool} | {count} |");
            sb.AppendLine();
        }

        if (TopErrorMessages.Count > 0)
        {
            sb.AppendLine("## Recent Errors");
            foreach (var err in TopErrorMessages)
                sb.AppendLine($"- {err}");
        }

        return sb.ToString();
    }
}

public sealed class ExperienceDebugger
{
    private readonly TaskJournal _journal;
    private readonly ActivityFeed _feed;

    public ExperienceDebugger(TaskJournal journal)
    {
        _journal = journal;
        _feed = ActivityFeed.Instance.Value;
    }

    public ExperienceReport Analyze(TimeSpan? window = null)
    {
        window ??= TimeSpan.FromHours(24);
        var cutoff = DateTime.UtcNow - window.Value;

        var journalEntries = _journal.Entries
            .Where(e => e.StartedAt > cutoff)
            .ToList();

        var failedEntries = journalEntries.Where(e => e.Status == JournalStatus.Failed).ToList();
        var activityEvents = _feed.Query(500)
            .Where(e => e.Timestamp > cutoff)
            .ToList();

        var patterns = ExtractPatterns(failedEntries, activityEvents);
        var toolErrors = ExtractToolErrors(activityEvents);

        return new ExperienceReport
        {
            TotalTraces = journalEntries.Count,
            ErrorCount = failedEntries.Count,
            ErrorRate = journalEntries.Count > 0 ? (double)failedEntries.Count / journalEntries.Count : 0,
            Patterns = patterns,
            ToolErrorCounts = toolErrors,
            TopErrorMessages = failedEntries.Take(10).Select(e => e.Error ?? e.Result ?? "unknown").ToList()
        };
    }

    private static List<FailurePattern> ExtractPatterns(
        List<JournalEntry> failedEntries,
        List<ActivityEvent> activityEvents)
    {
        var patterns = new List<FailurePattern>();

        var budgetErrors = failedEntries.Count(e => e.Error?.Contains("budget") == true || e.Error?.Contains("exceeded") == true);
        if (budgetErrors > 0)
            patterns.Add(new FailurePattern
            {
                Pattern = "Budget exceeded",
                RootCause = "Daily token/cost budget reached",
                OccurrenceCount = budgetErrors,
                Severity = budgetErrors > 10 ? "critical" : "high",
                SuggestedFix = "Add model degradation: deep→flash on budget tight, or increase daily limit"
            });

        var timeoutErrors = failedEntries.Count(e => e.Error?.Contains("timeout") == true || e.Error?.Contains("timed out") == true);
        if (timeoutErrors > 0)
            patterns.Add(new FailurePattern
            {
                Pattern = "Provider timeout",
                RootCause = "LLM provider response exceeded timeout threshold",
                OccurrenceCount = timeoutErrors,
                Severity = timeoutErrors > 5 ? "high" : "medium",
                SuggestedFix = "Enable ProviderFanOutRace for multi-provider redundancy"
            });

        var safetyBlocks = failedEntries.Count(e => e.Result?.StartsWith("[Safety") == true);
        if (safetyBlocks > 0)
            patterns.Add(new FailurePattern
            {
                Pattern = "Safety over-blocking",
                RootCause = "DNA safety or PromptShield too aggressive",
                OccurrenceCount = safetyBlocks,
                Severity = safetyBlocks > 10 ? "high" : "medium",
                SuggestedFix = "Audit blocked queries; adjust safety thresholds or add allowlist patterns"
            });

        var circuitBreakerErrors = failedEntries.Count(e => e.Error?.Contains("Circuit breaker") == true);
        if (circuitBreakerErrors > 0)
            patterns.Add(new FailurePattern
            {
                Pattern = "Circuit breaker tripping",
                RootCause = "Consecutive provider failures hitting circuit breaker threshold",
                OccurrenceCount = circuitBreakerErrors,
                Severity = "high",
                SuggestedFix = "Check provider health; increase circuit threshold; add fallback provider"
            });

        var emptyResponses = failedEntries.Count(e => string.IsNullOrWhiteSpace(e.Result) && string.IsNullOrWhiteSpace(e.Error));
        if (emptyResponses > 0)
            patterns.Add(new FailurePattern
            {
                Pattern = "Empty/null responses",
                RootCause = "LLM returned no content (possible API error or parsing failure)",
                OccurrenceCount = emptyResponses,
                Severity = "medium",
                SuggestedFix = "Add output validation in OutputGovernor; log raw API responses"
            });

        return patterns;
    }

    private static Dictionary<string, int> ExtractToolErrors(List<ActivityEvent> events)
    {
        return events
            .Where(e => e.Type == EventType.ToolCall && e.Severity >= EventSeverity.Error)
            .GroupBy(e => e.Agent)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
