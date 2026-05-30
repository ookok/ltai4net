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
                var localEmb = GetSharedEmbedder();
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

        // Skip KG query for casual chat — embedding-based intent classification
        if (!IsKnowledgeQuery(userMsg.Text))
        {
            _logger.LogDebug("KbGraph: skipped casual query \"{Q}\"", userMsg.Text);
            return context.AIContext!;
        }

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
        var nodeId = await _store.UpsertNode(
            extId: $"doc:{id}",
            kind: "document",
            name: title,
            ns: source,
            signature: $"len:{content.Length}",
            source: source);

        await _store.AddDoc(nodeId, content, lang, source);

        var concepts = ExtractConcepts(title, content);
        foreach (var concept in concepts.Take(15))
        {
            var cid = await _store.UpsertNode(
                extId: $"concept:{concept.ToLowerInvariant().Replace(" ", "_")}",
                kind: "concept",
                name: concept);
            await _store.AddEdge(nodeId, cid, "contains");
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
        var nodeId = await _store.UpsertNode(
            extId: $"fact:{id}",
            kind: "fact",
            name: content.Length > 100 ? content[..100] + "…" : content,
            ns: category,
            props: props);

        await _store.AddDoc(nodeId, content, "zh", source: "");

        if (sourceId != null)
        {
            var src = _store.GetNodeByExtId(sourceId);
            if (src != null) await _store.AddEdge(src.Id, nodeId, "has_fact");
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
    /// <summary>
    /// L0 短路判断：简单查询直接返回，不触发 LLM rewrite。
    /// 简单条件：≤4 个词、无特殊符号、无代码标记。
    /// </summary>
    private static bool IsSimpleQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 50) return false;
        var wordCount = query.Split([' ', '，', '。', '、'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4) return false;
        // 包含代码特殊字符 → 走 LLM
        if (query.Any(c => c is '_' or '.' or '/' or '\\' or '(' or ')' or '[' or ']' or '<' or '>'))
            return false;
        return true;
    }

    private async Task<string> ExpandQueryAsync(string query, CancellationToken ct)
    {
        // L0 短路：简单查询不触发 LLM
        if (_rewriter == null || IsSimpleQuery(query)) return query;
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

    /// <summary>
    /// Centroid embeddings for knowledge-seeking vs casual chat intent classification.
    /// Uses FastEmb (zero API cost, pure math) to decide whether a query needs KG lookup.
    /// </summary>
    private static readonly string[] KnowledgeAnchors =
    [
        "查找资料 搜索文档 查询信息 寻找代码",
        "什么是 是什么 怎么用 如何使用 如何实现",
        "为什么 原因 区别 对比 分析 比较",
        "代码在哪里 函数定义 方法实现 类结构",
        "解释一下 说明 介绍 总结 概括",
        "错误 问题 故障 异常 解决 修复",
        "配置 安装 部署 设置 参数 选项",
    ];

    private static readonly string[] SkipAnchors =
    [
        "你好 您好 hi hello hey 嗨 嘿嘿",
        "谢谢 感谢 多谢 辛苦了 好的 ok 嗯",
        "再见 拜拜 明天见 回头聊",
        "今天星期几 几点了 现在几点 今天几号",
        "1+1 一加一 算一下 计算",
        "在吗 在不在 有空吗 测试 试一下",
    ];

    private static float[]? _knowledgeCentroid;
    private static float[]? _skipCentroid;
    private static readonly object _centroidLock = new();

    private static void EnsureCentroids()
    {
        if (_knowledgeCentroid != null) return;
        lock (_centroidLock)
        {
            if (_knowledgeCentroid != null) return;
            _knowledgeCentroid = ComputeCentroid(KnowledgeAnchors);
            _skipCentroid = ComputeCentroid(SkipAnchors);
        }
    }

    private static float[] ComputeCentroid(string[] anchors)
    {
        const int dim = 384;
        var sum = new float[dim];
        int count = 0;
        foreach (var anchor in anchors)
        {
            var emb = LTAI.AI.EmbeddingClient.FastEmb(anchor, dim);
            for (int i = 0; i < dim; i++) sum[i] += emb[i];
            count++;
        }
        if (count > 0)
            for (int i = 0; i < dim; i++) sum[i] /= count;
        return sum;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    /// <summary>Intent-based KG gate. Uses FastEmb + cosine similarity.</summary>
    internal static bool IsKnowledgeQuery(string text)
    {
        EnsureCentroids();
        var emb = LTAI.AI.EmbeddingClient.FastEmb(text.Trim(), 384);
        var knowledgeScore = CosineSimilarity(emb, _knowledgeCentroid!);
        var skipScore = CosineSimilarity(emb, _skipCentroid!);
        return knowledgeScore > skipScore + 0.05f;
    }

    // 共享 LocalEmbedder 实例 — 避免每次查询都加载 90MB ONNX 模型
    private static readonly Lazy<LocalEmbedder> _sharedEmbedder = new(() => new LocalEmbedder(), true);

    private static LocalEmbedder GetSharedEmbedder() => _sharedEmbedder.Value;

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

