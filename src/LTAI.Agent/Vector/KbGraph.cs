// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Vector;

/// <summary>
/// Knowledge Base Graph (SQLite + FTS5).
/// Pipeline: LLM query rewrite → BM25 recall → CTE BFS expansion → context injection.
/// </summary>
public sealed class KbGraph : AIContextProvider
{
    private readonly KgStore _store;
    private readonly IChatClient? _rewriter;
    private readonly Reranker? _reranker;
    private readonly ILogger<KbGraph> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="store">SQLite KgStore.</param>
    /// <param name="rewriter">Optional LLM for query→keyword rewriting. If null, raw query is used as-is.</param>
    /// <param name="reranker">Optional two-stage reranker (embeddings + LLM rescore).</param>
    /// <param name="logger">Logger.</param>
    public KbGraph(KgStore store, IChatClient? rewriter = null,
        Reranker? reranker = null, ILogger<KbGraph>? logger = null)
        : base(null, null, null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rewriter = rewriter;
        _reranker = reranker;
        _logger = logger ?? NullLogger<KbGraph>.Instance;
    }

    // ═══════════════════════════════════════════
    //  Public query
    // ═══════════════════════════════════════════

    public async Task<List<string>> QueryAsync(string query, int topK = 10,
        bool expandGraph = true, CancellationToken ct = default)
    {
        // Stage 1: LLM query expansion → keywords + synonyms
        var expanded = await ExpandQueryAsync(query, ct);
        if (string.IsNullOrWhiteSpace(expanded)) expanded = query;

        _logger.LogInformation("KbGraph: \"{Q}\" → expanded: \"{E}\"", query, expanded);

        // Stage 2: FTS5 BM25 recall (weighted by node kind)
        var ftsHits = _store.SearchFts(expanded, topN: topK * 3);

        // Stage 2b: Optional hybrid search (FTS5 + sqlite-vec RRF)
        // Uses LocalEmbedder (BGE ONNX) for vector embeddings, no API key required.
        if (_reranker != null && ftsHits.Count > 0)
        {
            try
            {
                var localEmb = new LocalEmbedder();
                if (localEmb.Available)
                {
                    var queryEmb = localEmb.Generate(query);
                    var vecHits = _store.SearchVector(queryEmb, topN: topK * 3);

                    // RRF fusion: combine FTS5 BM25 + vector cosine distance ranks
                    var rrf = new Dictionary<long, double>();
                    int k = 60;
                    int rank = 0;
                    foreach (var h in ftsHits)
                        rrf[h.nodeId] = 1.0 / (k + rank++);
                    rank = 0;
                    foreach (var (nid, _) in vecHits)
                        rrf[nid] = rrf.GetValueOrDefault(nid) + 1.0 / (k + rank++);

                    var fusedIds = rrf.OrderByDescending(x => x.Value)
                                      .Take(topK * 2)
                                      .Select(x => x.Key)
                                      .ToHashSet();
                    ftsHits = ftsHits.Where(h => fusedIds.Contains(h.nodeId)).ToList();
                    _logger.LogInformation("KbGraph: FTS5+Vector RRF fusion, {N} results", ftsHits.Count);
                }
                localEmb.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KbGraph: hybrid search failed, using FTS5 only");
            }
        }

        // Stage 3: CTE BFS expansion
        HashSet<long> resultIds;
        if (expandGraph && ftsHits.Count > 0)
        {
            var startIds = ftsHits.Take(3).Select(h => h.nodeId).ToList();
            var bfsNodes = _store.TraverseBfs(startIds, maxDepth: 2, maxNodes: 10);
            resultIds = new HashSet<long>(bfsNodes.Select(n => n.Id));
            foreach (var h in ftsHits) resultIds.Add(h.nodeId);
        }
        else
        {
            resultIds = new HashSet<long>(ftsHits.Select(h => h.nodeId));
        }

        // Stage 4: Format output
        var seen = new HashSet<long>();
        var output = new List<string>();
        foreach (var nodeId in resultIds.Take(topK))
        {
            if (!seen.Add(nodeId)) continue;
            var node = _store.GetNode(nodeId);
            if (node == null) continue;

            output.Add(FormatNode(node));

            // Show related docs
            foreach (var doc in _store.GetDocs(nodeId).Take(2))
            {
                var snippet = doc.Text.Length > 200 ? doc.Text[..200] + "…" : doc.Text;
                output.Add($"  └─ {snippet}");
            }

            // Show neighbor edges
            foreach (var edge in _store.GetEdges(nodeId).Take(3))
            {
                var neighborId = edge.Src == nodeId ? edge.Dst : edge.Src;
                var neighbor = _store.GetNode(neighborId);
                if (neighbor != null)
                    output.Add($"  ══ {edge.Relation} ══ [{neighbor.Kind}] {neighbor.Name}");
            }
        }
        return output;
    }

    // ═══════════════════════════════════════════
    //  AIContextProvider
    // ═══════════════════════════════════════════

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 5)
            return context.AIContext!;

        try
        {
            var results = await QueryAsync(userMsg.Text, topK: 5, ct: ct);
            if (results.Count == 0) return context.AIContext!;

            var block = "## Relevant Knowledge:\n" + string.Join("\n", results.Select(r => "- " + r));
            _logger.LogInformation("KbGraph: injected {N} items", results.Count);

            return new AIContext
            {
                Instructions = context.AIContext?.Instructions != null
                    ? context.AIContext.Instructions + "\n\n" + block
                    : block,
                Messages = context.AIContext?.Messages,
                Tools = context.AIContext?.Tools,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph query failed");
            return context.AIContext!;
        }
    }

    // ═══════════════════════════════════════════
    //  Ingestion
    // ═══════════════════════════════════════════

    public async Task<string> IngestDocument(string id, string title, string content,
        string source = "", string lang = "zh")
    {
        var nodeId = _store.UpsertNode(
            extId: $"doc:{id}",
            kind: "document",
            name: title,
            ns: source,
            signature: $"len:{content.Length}",
            source: source);

        _store.AddDoc(nodeId, content, lang, source);

        var concepts = ExtractConcepts(title, content);
        foreach (var concept in concepts.Take(15))
        {
            var cid = _store.UpsertNode(
                extId: $"concept:{concept.ToLowerInvariant().Replace(" ", "_")}",
                kind: "concept",
                name: concept);
            _store.AddEdge(nodeId, cid, "contains");
        }

        _logger.LogInformation("KbGraph: ingested '{Id}' ({T}) with {C} concepts",
            id, title, concepts.Count);
        return $"Ingested '{title}' with {concepts.Count} concepts";
    }

    public async Task<string> IngestFact(string id, string content,
        string category = "general", string? sourceId = null)
    {
        var props = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["category"] = category
        };
        var nodeId = _store.UpsertNode(
            extId: $"fact:{id}",
            kind: "fact",
            name: content.Length > 100 ? content[..100] + "…" : content,
            ns: category,
            props: props);

        _store.AddDoc(nodeId, content, "zh", source: "");

        if (sourceId != null)
        {
            var src = _store.GetNodeByExtId(sourceId);
            if (src != null) _store.AddEdge(src.Id, nodeId, "has_fact");
        }
        return $"Ingested fact '{id}'";
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    /// <summary>
    /// LLM query expansion: generates 3 groups of search terms —
    /// core keywords, synonyms/related terms, and English equivalents (for Chinese queries).
    /// </summary>
    private async Task<string> ExpandQueryAsync(string query, CancellationToken ct)
    {
        if (_rewriter == null) return query;
        try
        {
            var prompt = $"""
                You are a search query expander. Given a query, produce expanded search terms.
                
                Rules:
                - Group 1: Core keywords from the original query (3-5 terms)
                - Group 2: Synonyms and related technical terms (2-4 terms)
                - Group 3: If the query is Chinese, add English equivalents (1-3 terms)
                
                Return ALL terms on a single line, space-separated.
                No explanations, no numbering.
                
                Examples:
                Query: 用户登录失败
                → login failure authentication UserService error 认证 失败 用户登录
                
                Query: 内存泄漏怎么排查
                → memory leak排查 GC dump heap allocation 内存 泄漏
                
                Query: {query}
                """;
            var resp = await _rewriter.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            var result = resp.Text?.Trim() ?? "";
            return string.IsNullOrWhiteSpace(result) ? query : result;
        }
        catch { return query; }
    }

    private static string FormatNode(NodeRow node)
    {
        var icon = node.Kind switch
        {
            "document" => "📄", "concept" => "🏷️", "fact" => "💡",
            _ => "▪️"
        };
        return $"{icon} [{node.Kind}] {node.Name}" +
               (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})");
    }

    private static List<string> ExtractConcepts(string title, string content)
    {
        return (title + " " + content)
            .Split([' ', '\n', '\r', ',', '.', '(', ')', '【', '】', '：', '，', '。'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }
}

