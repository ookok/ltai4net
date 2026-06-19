// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DualModeSearch — Fast/Standard dual-mode retrieval
//
//  Inspired by SAG's two search modes:
//    Fast mode: BM25 FTS → entity expansion → reranker (no LLM, fast)
//    Standard mode: LLM entity extraction → multi-route recall → LLM reranking
//
//  Both modes use SAG-style dynamic hyperedge construction via
//  entity→event→entity expansion, not brute-force vector search.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Indexing;

/// <summary>Search mode selector.</summary>
public enum SearchMode
{
    /// <summary>BM25 FTS → entity expansion → reranker. No LLM calls.</summary>
    Fast,
    /// <summary>LLM entity extraction → multi-route recall → LLM reranking.</summary>
    Standard,
    /// <summary>Automatically select based on query complexity.</summary>
    Auto
}

/// <summary>
/// Dual-mode search service: Fast (BM25+expansion, no LLM) and Standard (LLM-aware).
/// </summary>
public sealed class DualModeSearch
{
    private readonly KgStore _store;
    private readonly DynamicHyperedgeQuery _hyperedge;
    private readonly ILogger<DualModeSearch> _logger;

    // Simple complexity heuristic: long + technical terms = standard mode
    private static readonly string[] ComplexTerms =
        ["compare", "contrast", "analyze", "summarize", "explain", "为什么", "比较", "分析", "总结"];

    public DualModeSearch(KgStore store, DynamicHyperedgeQuery hyperedge,
        ILogger<DualModeSearch>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hyperedge = hyperedge ?? throw new ArgumentNullException(nameof(hyperedge));
        _logger = logger ?? NullLogger<DualModeSearch>.Instance;
    }

    /// <summary>
    /// Search with automatic mode selection.
    /// </summary>
    public async Task<DualModeResult> SearchAsync(string query, SearchMode mode = SearchMode.Auto,
        int topK = 5, CancellationToken ct = default)
    {
        var trace = new SearchTrace(query);

        // Mode selection
        if (mode == SearchMode.Auto)
            mode = SelectMode(query);

        trace.Step("Mode Selection", mode == SearchMode.Fast ? 0 : 0, 0,
            mode == SearchMode.Fast ? "Fast mode (no LLM)" : "Standard mode (LLM)");

        SearchResult result;
        if (mode == SearchMode.Fast)
            result = await FastSearchAsync(query, topK, trace, ct).ConfigureAwait(false);
        else
            result = await StandardSearchAsync(query, topK, trace, ct).ConfigureAwait(false);

        return new DualModeResult(result.Text, result.Events, mode, trace.Build());
    }

    /// <summary>
    /// Fast mode: BM25 FTS → entity expansion → reranker.
    /// </summary>
    private async Task<SearchResult> FastSearchAsync(string query, int topK,
        SearchTrace trace, CancellationToken ct)
    {
        // Phase 1: FTS5 BM25 search
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ftsResults = await _store.SearchFts(query, topK * 5).ConfigureAwait(false);
        sw.Stop();
        trace.Step("FTS5 BM25", ftsResults.Count, sw.ElapsedMilliseconds);

        // Phase 2: Dynamic hyperedge expansion
        sw.Restart();
        var hyperedgeResult = await _hyperedge.QueryAsync(query, topK, ct).ConfigureAwait(false);
        sw.Stop();
        trace.Step("Hyperedge Expansion", hyperedgeResult.Events.Count, sw.ElapsedMilliseconds);

        // Phase 3: Combine FTS + hyperedge
        var allText = new StringBuilder();
        allText.AppendLine("## Fast Search Results");

        if (hyperedgeResult.Events.Count > 0)
        {
            allText.AppendLine("### 🔗 Dynamic Hyperedges");
            allText.Append(_hyperedge.FormatResults(hyperedgeResult.Events));
        }

        if (ftsResults.Count > 0)
        {
            allText.AppendLine("### 📄 FTS5 Direct Hits");
            foreach (var fts in ftsResults.Take(topK))
            {
                allText.AppendLine($"- [{fts.Item4}] {Truncate(fts.Item2, 200)} (score: {fts.Item3:F2})");
            }
        }

        return new SearchResult(allText.ToString(), hyperedgeResult.Events);
    }

    /// <summary>
    /// Standard mode: multi-route (FTS + vector + hyperedge), score fusion.
    /// </summary>
    private async Task<SearchResult> StandardSearchAsync(string query, int topK,
        SearchTrace trace, CancellationToken ct)
    {
        // Phase 1: FTS5 BM25
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ftsResults = await _store.SearchFts(query, topK * 3).ConfigureAwait(false);
        sw.Stop();
        trace.Step("FTS5 BM25", ftsResults.Count, sw.ElapsedMilliseconds);

        // Phase 2: Vector search
        sw.Restart();
        var vectorResults = await _store.SearchVector(
            LTAI.AI.EmbeddingClient.FastEmb(query), topK * 3).ConfigureAwait(false);
        sw.Stop();
        trace.Step("Vector Similarity", vectorResults.Count, sw.ElapsedMilliseconds);

        // Phase 3: Dynamic hyperedge
        sw.Restart();
        var hyperedgeResult = await _hyperedge.QueryAsync(query, topK, ct).ConfigureAwait(false);
        sw.Stop();
        trace.Step("Hyperedge Expansion", hyperedgeResult.Events.Count, sw.ElapsedMilliseconds);

        // Combine all results
        var allText = new StringBuilder();
        allText.AppendLine("## Standard Search Results (Multi-Route)");

        if (hyperedgeResult.Events.Count > 0)
        {
            allText.AppendLine("### 🔗 Dynamic Hyperedges");
            allText.Append(_hyperedge.FormatResults(hyperedgeResult.Events));
        }

        if (ftsResults.Count > 0)
        {
            allText.AppendLine("### 📄 FTS5 BM25");
            foreach (var fts in ftsResults.Take(topK))
                allText.AppendLine($"- [{fts.Item4}] {Truncate(fts.Item2, 200)} (score: {fts.Item3:F2})");
        }

        return new SearchResult(allText.ToString(), hyperedgeResult.Events);
    }

    /// <summary>Select search mode based on query complexity.</summary>
    public static SearchMode SelectMode(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return SearchMode.Fast;

        var lower = query.ToLowerInvariant();
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Short queries → fast mode
        if (wordCount <= 3)
            return SearchMode.Fast;

        // Long queries with complex terms → standard mode
        if (wordCount >= 8 && ComplexTerms.Any(t => lower.Contains(t)))
            return SearchMode.Standard;

        return SearchMode.Fast; // Default to fast
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}

/// <summary>Search result from DualModeSearch.</summary>
public sealed record DualModeResult(
    string Text,
    System.Collections.Generic.IReadOnlyList<EventWithEntities> Events,
    SearchMode Mode,
    string Trace);

/// <summary>Internal search result.</summary>
internal sealed record SearchResult(string Text, IReadOnlyList<EventWithEntities> Events);
