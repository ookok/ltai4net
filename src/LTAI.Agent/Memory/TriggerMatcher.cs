// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  TriggerMatcher — keyword-based agent trigger matching
//
//  Matches user queries against agent trigger keywords (loaded from
//  agents/*.agent.md front-matter). Inspired by zap-coding-agent's
//  skill-trigger system: only inject context for agents whose trigger
//  keywords match the current query, reducing prompt bloat.
//
//  Usage:
//    1. MatchedAgents = TriggerMatcher.Match(query)
//    2. ExecutionEngine routes directly to matched agents
//    3. If no match, falls through to existing vector routing
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Agent.Memory;

/// <summary>
/// Result of trigger matching: which agents matched and their token estimates.
/// </summary>
public sealed record TriggerMatchResult(
    string AgentName,
    string Description,
    int TokenEstimate,
    float MatchScore);

/// <summary>
/// Matches user queries against agent trigger keywords loaded from
/// <c>agents/*.agent.md</c> front-matter. Registered as a DI singleton.
///
/// Implements multi-strategy matching:
///   - Exact word match (highest score)
///   - Substring word match
///   - Chinese character inclusion
///   - Score normalization to 0.0–1.0 range
/// </summary>
public sealed class TriggerMatcher
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly object _lock = new();
    private List<AgentTriggerEntry>? _entries;

    private sealed record AgentTriggerEntry(
        string Name,
        string Description,
        string[] Triggers,
        int TokenEstimate);

    public TriggerMatcher(IAgentRegistry agentRegistry)
    {
        _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
    }

    /// <summary>
    /// Match a user query against all registered agent triggers.
    /// Returns agents sorted by match score (highest first), capped at <paramref name="maxResults"/>.
    /// Returns empty list if no triggers match — caller should fall back to vector routing.
    /// </summary>
    public IReadOnlyList<TriggerMatchResult> Match(string query, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var entries = GetEntries();
        if (entries.Count == 0)
            return [];

        var queryLower = query.AsSpan();
        var words = Tokenize(query);

        var results = new List<(AgentTriggerEntry entry, float score)>();

        foreach (var entry in entries)
        {
            var score = ComputeMatchScore(queryLower, words, entry.Triggers);
            if (score > 0f)
                results.Add((entry, score));
        }

        if (results.Count == 0)
            return [];

        // Sort by score descending, then by token estimate (prefer smaller = more focused)
        results.Sort((a, b) =>
        {
            var cmp = b.score.CompareTo(a.score);
            return cmp != 0 ? cmp : a.entry.TokenEstimate.CompareTo(b.entry.TokenEstimate);
        });

        return results
            .Take(maxResults)
            .Select(r => new TriggerMatchResult(
                r.entry.Name,
                r.entry.Description,
                r.entry.TokenEstimate,
                r.score))
            .ToList();
    }

    /// <summary>
    /// Get the first matching agent's name, or null if no match.
    /// Convenience for simple "route to this agent" scenarios.
    /// </summary>
    public string? MatchFirst(string query)
    {
        var matched = Match(query, maxResults: 1);
        return matched.Count > 0 ? matched[0].AgentName : null;
    }

    /// <summary>
    /// Check if any trigger matches the query (cheaper than full Match).
    /// </summary>
    public bool IsMatch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var entries = GetEntries();
        if (entries.Count == 0)
            return false;

        var queryLower = query.AsSpan();
        var words = Tokenize(query);

        foreach (var entry in entries)
        {
            if (ComputeMatchScore(queryLower, words, entry.Triggers) > 0f)
                return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    private List<AgentTriggerEntry> GetEntries()
    {
        if (_entries != null) return _entries;
        lock (_lock)
        {
            if (_entries != null) return _entries;
            var defs = _agentRegistry.LoadAll();
            var entries = new List<AgentTriggerEntry>(defs.Count);
            foreach (var def in defs)
            {
                if (def.Trigger.Length > 0 && !string.IsNullOrEmpty(def.Name))
                {
                    entries.Add(new AgentTriggerEntry(
                        def.Name,
                        def.Description,
                        def.Trigger,
                        def.TokenEstimate));
                }
            }
            _entries = entries;
            return entries;
        }
    }

    internal void InvalidateCache()
    {
        lock (_lock) _entries = null;
    }

    private static float ComputeMatchScore(ReadOnlySpan<char> queryLower, string[] words, string[] triggers)
    {
        float maxScore = 0f;

        foreach (var trigger in triggers)
        {
            var triggerLowerStr = trigger.ToLowerInvariant();
            float score = 0f;

            // 1. Exact multi-word trigger match (e.g. "code review", "pull request")
            if (triggerLowerStr.Contains(' '))
            {
                if (queryLower.Contains(triggerLowerStr.AsSpan(), StringComparison.Ordinal))
                {
                    score = 0.95f;
                }
                else
                {
                    var triggerWords = triggerLowerStr.Split(' ');
                    var matchedWords = 0;
                    foreach (var tw in triggerWords)
                    {
                        foreach (var w in words)
                        {
                            if (string.Equals(w, tw, StringComparison.Ordinal))
                            {
                                matchedWords++;
                                break;
                            }
                        }
                    }
                    if (matchedWords > 0)
                        score = 0.5f * (float)matchedWords / triggerWords.Length;
                }
            }
            else
            {
                // 2. Single-word trigger: check if word appears in query
                foreach (var w in words)
                {
                    if (string.Equals(w, triggerLowerStr, StringComparison.Ordinal))
                    {
                        score = Math.Min(0.9f, 0.5f + trigger.Length * 0.02f);
                        break;
                    }
                }

                if (score == 0f && queryLower.Contains(triggerLowerStr.AsSpan(), StringComparison.Ordinal))
                    score = 0.3f;
            }

            if (score > maxScore)
                maxScore = score;
        }

        return maxScore;
    }

    /// <summary>Split query into tokens: lowercase, split on non-alphanumeric, filter short tokens.</summary>
    private static string[] Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Split on non-alphanumeric chars (including CJK which we keep as single-char tokens)
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                // Keep CJK characters as single-char tokens
                if (ch > 0x4E00 && ch < 0x9FFF)
                    tokens.Add(new string(ch, 1));
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());

        // Filter: keep tokens >= 2 chars, or CJK single chars
        return tokens
            .Where(t => t.Length >= 2 || (t.Length == 1 && t[0] > 0x4E00 && t[0] < 0x9FFF))
            .Distinct()
            .ToArray();
    }
}
