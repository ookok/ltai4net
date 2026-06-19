// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DynamicHyperedgeQuery — SAG-style query-time hyperedge construction
//
//  Inspired by SAG (SQL-Retrieval Augmented Generation, arXiv 2606.15971).
//  Instead of pre-building a global static graph with edges between all
//  entities, SAG converts each chunk into one event + N entities, then
//  uses SQL JOIN at query time to dynamically link events that share
//  entities into local hyperedges.
//
//  Uses the existing KgStore public API (SearchFts, SearchNodesByName,
//  GetNode, GetDocs) — no direct SQL commands needed.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Indexing;

/// <summary>
/// Result of a dynamic hyperedge query.
/// </summary>
public sealed record HyperedgeResult
{
    public IReadOnlyList<EventWithEntities> Events { get; init; } = [];
    public string Trace { get; init; } = "";
    public long LatencyMs { get; init; }
}

/// <summary>One event with its linked entities.</summary>
public sealed record EventWithEntities(
    long EventId,
    string EventName,
    string EventText,
    string Source,
    IReadOnlyList<string> Entities,
    float RelevanceScore);

/// <summary>
/// SAG-style dynamic hyperedge constructor using existing KgStore API.
/// </summary>
public sealed class DynamicHyperedgeQuery
{
    private readonly KgStore _store;
    private readonly ILogger<DynamicHyperedgeQuery> _logger;

    private const int MaxHopDepth = 3;
    private const int MaxEntitiesPerHop = 10;
    private const int MaxEvents = 15;

    public DynamicHyperedgeQuery(KgStore store, ILogger<DynamicHyperedgeQuery>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<DynamicHyperedgeQuery>.Instance;
    }

    /// <summary>
    /// Run a dynamic hyperedge query.
    /// 1. FTS5 BM25 to find seed entities/events
    /// 2. SQL JOIN to link events sharing entities
    /// 3. Multi-hop recursive expansion
    /// 4. Score and rank
    /// </summary>
    public async Task<HyperedgeResult> QueryAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var traceSb = new StringBuilder();
        traceSb.AppendLine($"## Dynamic Hyperedge Query");
        traceSb.AppendLine($"Query: {query}");
        traceSb.AppendLine();

        // Phase 1: FTS5 BM25 to find seed entities and events
        traceSb.AppendLine("### Phase 1: FTS5 BM25 Entity/Event Search");
        var seeds = await FindSeedsAsync(query, topK * 3, ct).ConfigureAwait(false);
        traceSb.AppendLine($"Found {seeds.Count} seed candidates");

        if (seeds.Count == 0)
        {
            sw.Stop();
            return new HyperedgeResult { Events = [], Trace = traceSb.ToString(), LatencyMs = sw.ElapsedMilliseconds };
        }

        // Phase 2: Build hyperedge — find events linked to seed entities
        traceSb.AppendLine("### Phase 2: Hyperedge Assembly");
        var hyperedge = await BuildHyperedgeAsync(seeds, ct).ConfigureAwait(false);
        traceSb.AppendLine($"Assembled {hyperedge.Count} event+entity groups");

        // Phase 3: Multi-hop expansion
        traceSb.AppendLine("### Phase 3: Multi-hop Expansion");
        var expanded = await ExpandAsync(hyperedge, MaxHopDepth, ct).ConfigureAwait(false);
        traceSb.AppendLine($"After expansion: {expanded.Count} groups");

        // Phase 4: Score and rank
        traceSb.AppendLine("### Phase 4: Ranking");
        var scored = ScoreAndRank(expanded, query);
        var final = scored.Take(topK).ToList();
        traceSb.AppendLine($"Returning {final.Count} results");

        sw.Stop();
        traceSb.AppendLine($"**Total: {sw.ElapsedMilliseconds}ms**");

        return new HyperedgeResult { Events = final, Trace = traceSb.ToString(), LatencyMs = sw.ElapsedMilliseconds };
    }

    /// <summary>
    /// Get a trace string for the last query (for debugging context injection).
    /// </summary>
    public async Task<string> ExplainAsync(string query, CancellationToken ct = default)
    {
        var result = await QueryAsync(query, topK: 3, ct).ConfigureAwait(false);
        return result.Trace + "\n\n" + FormatResults(result.Events);
    }

    /// <summary>
    /// Format results as a compact Markdown context block.
    /// </summary>
    public string FormatResults(IReadOnlyList<EventWithEntities> events)
    {
        if (events.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## Dynamic Hyperedge Context");
        sb.AppendLine();

        foreach (var evt in events.Take(5))
        {
            sb.AppendLine($"### {evt.EventName}");
            if (evt.Entities.Count > 0)
                sb.AppendLine($"*Entities: {string.Join(", ", evt.Entities)}*");
            sb.AppendLine();
            sb.AppendLine(TruncateText(evt.EventText, 400));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ─── Private ───

    /// <summary>Find seed entities/events via FTS5 BM25.</summary>
    private async Task<List<(long nodeId, string name, string kind, string text, float score)>>
        FindSeedsAsync(string query, int limit, CancellationToken ct)
    {
        try
        {
            var ftsResults = await _store.SearchFts(query, limit).ConfigureAwait(false);
            var results = new List<(long, string, string, string, float)>();

            foreach (var result in ftsResults)
            {
                var nodeId = result.Item1;
                var text = result.Item2;
                var rank = result.Item3;
                var kind = result.Item4;

                // Only entity/event nodes are hyperedge anchors
                if (kind != "entity" && kind != "event") continue;

                // Get node details
                var node = await _store.GetNode(nodeId).ConfigureAwait(false);
                if (node == null) continue;

                // Boost entity scores (entities are primary index anchors)
                var boost = kind == "entity" ? 2.0f : 1.5f;
                results.Add((nodeId, node.Name, kind, text, (float)rank * boost));
            }

            return results.OrderByDescending(r => r.Item5).Take(limit).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DynamicHyperedgeQuery: seed search failed");
            return [];
        }
    }

    /// <summary>Build hyperedge: find events linked to seed entities.</summary>
    private async Task<List<EventWithEntities>> BuildHyperedgeAsync(
        List<(long nodeId, string name, string kind, string text, float score)> seeds,
        CancellationToken ct)
    {
        var eventMap = new Dictionary<long, EventBuilder>();

        foreach (var seed in seeds)
        {
            if (seed.kind == "event")
            {
                if (!eventMap.ContainsKey(seed.nodeId))
                    eventMap[seed.nodeId] = new EventBuilder(seed.name, seed.text, ExtractSource(seed.text), seed.score);
                continue;
            }

            // Entity match: find events linked to this entity via event name match
            var linked = await FindEventsByNameAsync(seed.name, ct).ConfigureAwait(false);
            foreach (var (evId, evName, evText, evSource) in linked)
            {
                if (eventMap.TryGetValue(evId, out var eb))
                {
                    eb.Entities.Add(seed.name);
                    eb.TotalScore += seed.score * 0.5f;
                }
                else
                {
                    eventMap[evId] = new EventBuilder(evName, evText, evSource, seed.score * 0.5f)
                    { Entities = new HashSet<string> { seed.name } };
                }
            }
        }

        return eventMap
            .Select(kv => kv.Value.ToResult(kv.Key))
            .OrderByDescending(e => e.RelevanceScore)
            .Take(MaxEvents)
            .ToList();
    }

    /// <summary>Find events whose name or text mentions a given entity name.</summary>
    private async Task<List<(long id, string name, string text, string source)>>
        FindEventsByNameAsync(string entityName, CancellationToken ct)
    {
        try
        {
            var matching = await _store.SearchNodesByName($"%{entityName}%", limit: 10).ConfigureAwait(false);
            var results = new List<(long, string, string, string)>();

            foreach (var node in matching)
            {
                if (node.Kind != "event") continue;
                var docs = await _store.GetDocs(node.Id).ConfigureAwait(false);
                var text = docs.Count > 0 ? docs[0].Text : "";
                results.Add((node.Id, node.Name, text, node.Source ?? ""));
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DynamicHyperedgeQuery: entity→event search failed");
            return [];
        }
    }

    /// <summary>Multi-hop expansion via shared entities.</summary>
    private async Task<List<EventWithEntities>> ExpandAsync(
        List<EventWithEntities> initial, int maxDepth, CancellationToken ct)
    {
        if (maxDepth <= 0 || initial.Count == 0) return initial;

        var visited = new HashSet<long>(initial.Select(e => e.EventId));
        var frontier = new List<EventWithEntities>(initial);

        for (int hop = 0; hop < maxDepth; hop++)
        {
            var entityNames = frontier
                .SelectMany(e => e.Entities)
                .Distinct()
                .Take(MaxEntitiesPerHop)
                .ToList();

            if (entityNames.Count == 0) break;

            var newEvents = new List<EventWithEntities>();
            foreach (var entityName in entityNames)
            {
                var linked = await FindEventsByNameAsync(entityName, ct).ConfigureAwait(false);
                foreach (var (evId, evName, evText, evSource) in linked)
                {
                    if (visited.Add(evId))
                    {
                        newEvents.Add(new EventWithEntities(
                            evId, evName, evText, evSource, [entityName],
                            0.3f / (hop + 2)));
                    }
                }
            }

            if (newEvents.Count == 0) break;
            frontier = newEvents;
            initial.AddRange(newEvents);
        }
        return initial;
    }

    /// <summary>Score and rank by entity overlap and query term matching.</summary>
    private static List<EventWithEntities> ScoreAndRank(List<EventWithEntities> events, string query)
    {
        var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return events.Select(e =>
        {
            var textLower = e.EventText.ToLowerInvariant();
            var entityOverlap = e.Entities.Count(ent =>
                queryTerms.Any(qt => ent.Contains(qt, StringComparison.OrdinalIgnoreCase)));
            var textOverlap = queryTerms.Count(qt => textLower.Contains(qt));

            return e with { RelevanceScore = e.RelevanceScore + entityOverlap * 0.15f + textOverlap * 0.1f };
        })
        .OrderByDescending(e => e.RelevanceScore)
        .ToList();
    }

    /// <summary>Extract source identifier from text.</summary>
    private static string ExtractSource(string text)
    {
        if (string.IsNullOrEmpty(text)) return "event";
        foreach (var line in text.Split('\n').Take(3))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("://") || trimmed.Contains('/') || trimmed.Contains('\\'))
                return trimmed.Truncate(80);
        }
        return "event";
    }

    private static string TruncateText(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";

    /// <summary>Mutable builder for hyperedge assembly.</summary>
    private sealed class EventBuilder
    {
        public string Name;
        public string Text;
        public string Source;
        public float TotalScore;
        public HashSet<string> Entities = new();

        public EventBuilder(string name, string text, string source, float score)
        {
            Name = name; Text = text; Source = source; TotalScore = score;
        }

        public EventWithEntities ToResult(long id) => new(id, Name, Text, Source, Entities.ToList(), TotalScore);
    }
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
