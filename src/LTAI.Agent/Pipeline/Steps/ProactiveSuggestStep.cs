// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ProactiveSuggestStep — pipeline step for code suggestions
//
//  Inspiration: TIDE (arXiv 2606.04743)
//
//  Injects code quality suggestions into the conversation context
//  when the query is code-related. The step checks the
//  ProactiveSuggestService for cached results and prepends
//  relevant suggestions as context.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Suggestions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects code quality suggestions from the
/// ProactiveSuggestService into the message context.
///
/// Only activates when:
///   1. The query is code-related (detected via heuristics)
///   2. The suggestion service has cached results
///   3. Suggestions haven't been shown recently
/// </summary>
public sealed class ProactiveSuggestStep : IPipelineStep
{
    private readonly Suggestions.ProactiveSuggestService? _suggestService;
    private readonly ILogger<ProactiveSuggestStep> _logger;

    public string Name => "ProactiveSuggest";

    public ProactiveSuggestStep(
        Suggestions.ProactiveSuggestService? suggestService = null,
        ILogger<ProactiveSuggestStep>? logger = null)
    {
        _suggestService = suggestService;
        _logger = logger ?? NullLogger<ProactiveSuggestStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_suggestService == null)
        {
            _logger.LogDebug("ProactiveSuggestStep: no service registered, skipping");
            return context;
        }

        // Only activate for code-related queries
        if (!IsCodeRelated(context.Request))
        {
            _logger.LogTrace("ProactiveSuggestStep: query not code-related, skipping");
            return context;
        }

        // Check if suggestions already shown
        if (context.TryGet("_suggestionsShown", out bool shown) && shown)
        {
            _logger.LogTrace("ProactiveSuggestStep: suggestions already shown, skipping");
            return context;
        }

        // Mark user as active (prevents background scan during chat)
        _suggestService.MarkActive();

        // Get cached suggestions
        var suggestions = _suggestService.LastResults;
        if (suggestions == null || suggestions.Count == 0)
        {
            _logger.LogDebug("ProactiveSuggestStep: no suggestions available");
            return context;
        }

        // Build suggestion context
        var contextText = BuildSuggestionContext(suggestions);
        if (!string.IsNullOrEmpty(contextText))
        {
            context.Messages.Insert(0, new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System, contextText));
            context.Set("_suggestionsShown", true);

            _logger.LogInformation(
                "ProactiveSuggestStep: injected {Count} code suggestions into context",
                suggestions.Count);
        }

        return await Task.FromResult(context);
    }

    private static bool IsCodeRelated(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var lower = query.ToLowerInvariant();
        return lower.Contains("code") || lower.Contains("function") || lower.Contains("class")
            || lower.Contains("bug") || lower.Contains("fix") || lower.Contains("refactor")
            || lower.Contains("review") || lower.Contains("debug") || lower.Contains("implement")
            || lower.Contains("method") || lower.Contains("test") || lower.Contains("todo");
    }

    private static string BuildSuggestionContext(IReadOnlyList<CodeIssue> suggestions)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Code Quality Suggestions]");
        sb.AppendLine($"Found {suggestions.Count} issues in workspace:");

        // Group by category
        var grouped = suggestions.GroupBy(i => i.Category);
        foreach (var group in grouped)
        {
            var critical = group.Count(i => i.Severity == IssueSeverity.Critical);
            var warnings = group.Count(i => i.Severity == IssueSeverity.Warning);
            var icon = critical > 0 ? "🔴" : warnings > 0 ? "🟡" : "🟢";
            sb.AppendLine($"{icon} {group.Key}: {group.Count()} ({critical} critical, {warnings} warnings)");
        }

        // Top 5 most important
        var top = suggestions
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.File)
            .Take(5)
            .ToList();

        if (top.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Top suggestions (showing {top.Count}/{suggestions.Count}, sorted by severity):");
            foreach (var issue in top)
            {
                var severity = issue.Severity switch
                {
                    IssueSeverity.Critical => "[CRITICAL]",
                    IssueSeverity.Warning => "[WARNING]",
                    _ => "[INFO]",
                };
                sb.AppendLine($"- {severity} {issue.Title} ({issue.File}:{issue.Line})");
                if (issue.Suggestion != null)
                    sb.AppendLine($"  Tip: {issue.Suggestion}");
            }
        }

        return sb.ToString();
    }
}
