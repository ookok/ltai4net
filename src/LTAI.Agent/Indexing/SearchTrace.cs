// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SearchTrace — retrieval step recorder for observability
//
//  Records each step of a retrieval pipeline (FTS, vector, hyperedge,
//  reranker) with timing and hit counts. Exposes a compact trace
//  string for TUI/Web display, similar to SAG's right-side trace panel.
//
//  Usage:
//    var trace = new SearchTrace("my query");
//    trace.Step("FTS5 BM25", hits: 42, latencyMs: 15);
//    trace.Step("Hyperedge expand", hits: 8, latencyMs: 32);
//    Console.WriteLine(trace.Build());
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTAI.Agent.Indexing;

/// <summary>
/// Records a single step in a retrieval pipeline.
/// </summary>
public sealed record SearchTraceStep(
    string Name,
    int Candidates,
    long LatencyMs,
    string? Detail = null);

/// <summary>
/// Retrieval trace recorder. Thread-safe.
/// </summary>
public sealed class SearchTrace
{
    private readonly string _query;
    private readonly List<SearchTraceStep> _steps = [];
    private readonly object _lock = new();
    private readonly DateTime _startedAt;

    public string Query => _query;
    public IReadOnlyList<SearchTraceStep> Steps { get { lock (_lock) return _steps.ToList(); } }
    public DateTime StartedAt => _startedAt;

    public SearchTrace(string query)
    {
        _query = query ?? "";
        _startedAt = DateTime.UtcNow;
    }

    /// <summary>Record a retrieval step.</summary>
    public void Step(string name, int candidates, long latencyMs, string? detail = null)
    {
        lock (_lock)
        {
            _steps.Add(new SearchTraceStep(name, candidates, latencyMs, detail));
        }
    }

    /// <summary>Build a compact trace string (Markdown).</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 🔍 Search Trace");
        sb.AppendLine($"Query: `{_query.Truncate(60)}`");
        sb.AppendLine();

        IReadOnlyList<SearchTraceStep> steps;
        lock (_lock) { steps = _steps.ToList(); }

        if (steps.Count == 0)
        {
            sb.AppendLine("*(no retrieval steps recorded)*");
            return sb.ToString();
        }

        long totalMs = 0;
        foreach (var step in steps)
        {
            totalMs += step.LatencyMs;
            var icon = step.Name.Contains("FTS") || step.Name.Contains("BM25") ? "📄"
                     : step.Name.Contains("Vector") || step.Name.Contains("Embed") ? "🧠"
                     : step.Name.Contains("Hyperedge") || step.Name.Contains("Expand") ? "🔗"
                     : step.Name.Contains("Rerank") || step.Name.Contains("Rank") ? "⚖️"
                     : step.Name.Contains("LLM") ? "🤖"
                     : "➡️";

            sb.AppendLine($"{icon} **{step.Name}** — {step.Candidates} candidates, {step.LatencyMs}ms");
            if (!string.IsNullOrEmpty(step.Detail))
                sb.AppendLine($"   └─ {step.Detail}");
        }

        sb.AppendLine();
        sb.AppendLine($"**Total: {totalMs}ms, {steps.Count} steps**");
        return sb.ToString();
    }

    /// <summary>Build a compact one-line summary (for status bar).</summary>
    public string Summary()
    {
        IReadOnlyList<SearchTraceStep> steps;
        lock (_lock) { steps = _steps.ToList(); }

        if (steps.Count == 0) return "";
        var last = steps[^1];
        long totalMs = steps.Sum(s => s.LatencyMs);
        int totalCandidates = steps.Sum(s => s.Candidates);
        return $"🔍 {steps.Count} steps · {totalCandidates} candidates · {totalMs}ms";
    }
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
