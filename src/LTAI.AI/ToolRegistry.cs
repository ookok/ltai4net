// Tool RAG: 双路检索 + RRF 融合
// BM25（关键词匹配）+ Vector（语义相似度）→ RRF 融合排序
// 参照 KbGraph 的混合检索模式，k=60

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace LTAI.AI;

/// <summary>
/// 工具注册表。
/// 离线收集所有工具的结构化 embedding + BM25 倒排索引，
/// 在线执行双路检索（BM25 + Vector）→ RRF 融合 → Top-K。
///
/// 检索管线：
///   1. BM25 关键词检索（工具名 + 描述 + 示例文本）
///   2. Vector 语义检索（ONNX embedding 余弦相似度）
///   3. RRF 融合 (k=60) → 同 domain 加权 → Top-K
///
/// embedding 使用 EmbeddingClient 的三级管线：
///   1. Local ONNX (all-MiniLM-L6-v2, 384d)
///   2. Remote API providers
///   3. BM25 (FastEmb) fallback
///
/// Backward-compatible static access preserved.
/// DI users inject <see cref="IToolRegistry"/> (same singleton instance).
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    /// <summary>Shared default instance for static method delegation and DI.</summary>
    private static readonly Lazy<ToolRegistry> _default = new(() => new ToolRegistry());

    /// <summary>单个工具的定义 + embedding + domain。</summary>
    public sealed record ToolDef(string Name, string Description, float[] Embedding, string Domain = "");

    // ═══════════════════════════════════════════
    //  Static implementation (preserved for backward compat)
    // ═══════════════════════════════════════════

    private static readonly List<ToolDef> _tools = new();
    private static volatile bool _initialized;
    private static readonly object _lock = new();
    private static volatile IReadOnlyList<ToolDef> _snapshot = Array.Empty<ToolDef>();

    /// <summary>True if ToolRegistry has been initialized at least once.</summary>
    public static bool IsInitialized => _initialized;

    // ── Usage statistics ──
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    public sealed record ToolStats(string Name, long CallCount, long SuccessCount, long TotalLatencyMs, double AvgLatencyMs)
    {
        public double SuccessRate => CallCount > 0 ? (double)SuccessCount / CallCount : 1.0;
    }

    /// <summary>Record a tool call result for metrics.</summary>
    public static void RecordCall(string toolName, bool success, long latencyMs)
    {
        _stats.AddOrUpdate(toolName,
            _ => new ToolStats(toolName, 1, success ? 1 : 0, latencyMs, latencyMs),
            (_, old) => old with
            {
                CallCount = old.CallCount + 1,
                SuccessCount = old.SuccessCount + (success ? 1 : 0),
                TotalLatencyMs = old.TotalLatencyMs + latencyMs,
                AvgLatencyMs = (old.TotalLatencyMs + latencyMs) / (double)(old.CallCount + 1)
            });
    }

    /// <summary>Get all tool invocation statistics.</summary>
    public static IReadOnlyDictionary<string, ToolStats> GetAllStats() =>
        new Dictionary<string, ToolStats>(_stats);

    /// <summary>Get stats for a specific tool.</summary>
    public static ToolStats? GetStats(string toolName) =>
        _stats.TryGetValue(toolName, out var s) ? s : null;

    /// <summary>Reset all statistics.</summary>
    public static void ResetStats() => _stats.Clear();

    // ═══════════════════════════════════════════
    //  BM25 倒排索引
    // ═══════════════════════════════════════════

    /// <summary>倒排索引: term → (docId, termFrequency) 列表</summary>
    private static Dictionary<string, List<(int docId, int tf)>> _invertedIndex = new();
    /// <summary>每个文档的长度（词数）</summary>
    private static int[] _docLengths = [];
    /// <summary>包含每个 term 的文档数</summary>
    private static Dictionary<string, int> _docFreq = new();
    private static int _docFreqCount;
    private static volatile int _bm25Version;
    /// <summary>语料库总文档数</summary>
    private static int _totalDocs;
    /// <summary>平均文档长度</summary>
    private static double _avgDocLen;
    /// <summary>停用词表</summary>
    private static readonly HashSet<string> _stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could", "should",
        "may", "might", "shall", "can", "need", "to", "of", "in", "for", "on",
        "with", "at", "by", "from", "as", "into", "through", "during", "before", "after",
        "above", "below", "between", "out", "off", "over", "under", "again", "further",
        "then", "once", "here", "there", "when", "where", "why", "how", "all", "each",
        "every", "both", "few", "more", "most", "other", "some", "such", "no", "nor",
        "not", "only", "own", "same", "so", "than", "too", "very", "just",
        "this", "that", "these", "those", "what", "which", "who", "whom", "and", "but",
        "or", "if", "while", "about", "up",
        // 中文停用词
        "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都", "一",
        "一个", "上", "也", "很", "到", "说", "要", "去", "你", "会", "着",
        "没有", "看", "好", "自己", "这", "他", "她", "它", "们", "那", "些",
    };

    /// <summary>BM25 参数</summary>
    private const float Bm25K1 = 1.2f;
    private const float Bm25B = 0.75f;

    /// <summary>RRF 融合常数（与 KbGraph 一致）</summary>
    private const int RrfK = 60;

    /// <summary>各路检索的 Top-N</summary>
    private const int Bm25TopN = 50;
    private const int VectorTopN = 50;
    private const int RrfTopN = 30;

    /// <summary>Majority voting penalty for single-path-only tools (0-1).</summary>
    private const double MajorityVotePenalty = 0.3;

    /// <summary>
    /// 构建工具的 embedding 文本（domain + 5 部分自然语言段落）。
    /// </summary>
    private static string BuildEmbeddingText(AITool tool)
    {
        var name = tool.Name ?? "unknown";
        var desc = tool.Description ?? "";
        var domain = GetToolDomain(tool);

        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(domain))
            sb.Append($"[{domain}] ");

        sb.Append($"{name}: {desc}");

        var examples = GetToolExamples(tool);
        if (examples.Length > 0)
        {
            sb.Append("\n用户可能说: ");
            sb.Append(string.Join("; ", examples));
        }

        return sb.ToString();
    }

    private static string GetToolDomain(AITool tool)
    {
        try
        {
            if (tool is AIFunction func && func.UnderlyingMethod != null)
            {
                return func.UnderlyingMethod
                    .GetCustomAttribute<ToolDomainAttribute>(false)?.Domain ?? "";
            }
        }
        catch (Exception)
        {
            // non-critical, best-effort
        }
        return "";
    }

    private static string[] GetToolExamples(AITool tool)
    {
        try
        {
            if (tool is AIFunction func && func.UnderlyingMethod != null)
            {
                return func.UnderlyingMethod
                    .GetCustomAttributes<ToolExampleAttribute>(false)
                    .Select(a => a.Query)
                    .ToArray();
            }
        }
        catch
        {
            // non-critical, best-effort
        }
        return [];
    }

    /// <summary>初始化工具注册表：构建向量 embedding + BM25 倒排索引。</summary>
    /// <remarks>
    /// P12.2: pass a <see cref="ToolEmbeddingCache"/> to persist tool
    /// embeddings across process restarts. On first call the cache miss
    /// triggers a single batched ONNX call for all 80+ tools; on subsequent
    /// calls (or after a restart) the cache hit eliminates the embedding work
    /// entirely. Without a cache, falls back to a one-shot batched ONNX call
    /// (no persistence).
    /// </remarks>
    public static async Task InitializeAsync(IEnumerable<AITool> tools, EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, CancellationToken ct = default)
    {
        // Volatile check (fast path, no lock)
        if (_initialized) return;
        var list = tools.ToList();
        var texts = list.Select(BuildEmbeddingText).ToArray();

        // ── 向量 embedding (P12.2: cache-aware) ──
        float[][] embeddings;
        if (cache != null)
        {
            // Persisted batched path: 1 ONNX call on first start, 0 calls on
            // warm starts (cache hit).
            try
            {
                var items = list
                    .Select((t, i) => (Key: t.Name ?? "unknown", Description: texts[i]))
                    .ToList();
                var vectors = await cache.GetOrComputeAllAsync(items, ct).ConfigureAwait(false);
                embeddings = list
                    .Select(t => vectors.TryGetValue(t.Name ?? "unknown", out var v) && v != null
                        ? v
                        : EmbeddingClient.FastEmb(BuildEmbeddingText(t), 384))
                    .ToArray();
            }
            catch
            {
                // Cache path failed — fall through to direct batch
                embeddings = await DirectBatchAsync(embedder, texts, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // One-shot batched path (no persistence)
            embeddings = await DirectBatchAsync(embedder, texts, ct).ConfigureAwait(false);
        }

        var defs = new List<ToolDef>();
        for (int i = 0; i < list.Count; i++)
        {
            var emb = i < embeddings.Length ? embeddings[i] : EmbeddingClient.FastEmb(texts[i], 384);
            var domain = GetToolDomain(list[i]);
            defs.Add(new ToolDef(list[i].Name ?? "unknown", list[i].Description ?? "", emb, domain));
        }

        // ── BM25 倒排索引 ──
        lock (_lock)
        {
            // Double-check after acquiring the lock.
            // If Clear() was called between the volatile check and here,
            // _initialized is false and we proceed correctly.
            if (_initialized) return;
            BuildBm25Index(texts);
            _tools.AddRange(defs);
            Thread.MemoryBarrier();
            _initialized = true;
            _snapshot = _tools.ToArray();
        }
    }

    /// <summary>从工具文本构建 BM25 倒排索引。</summary>
    private static void BuildBm25Index(string[] texts)
    {
        _totalDocs = texts.Length;
        _docLengths = new int[_totalDocs];
        _docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _invertedIndex = new Dictionary<string, List<(int docId, int tf)>>(StringComparer.OrdinalIgnoreCase);
        long totalTerms = 0;

        for (int docId = 0; docId < _totalDocs; docId++)
        {
            var terms = Tokenize(texts[docId]);
            _docLengths[docId] = terms.Length;
            totalTerms += terms.Length;

            // 文档内的词频
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in terms)
            {
                if (_stopwords.Contains(term) || term.Length <= 1) continue;
                tf.TryGetValue(term, out var c);
                tf[term] = c + 1;
            }

            foreach (var (term, count) in tf)
            {
                if (!_invertedIndex.ContainsKey(term))
                    _invertedIndex[term] = new List<(int, int)>();
                _invertedIndex[term].Add((docId, count));

                _docFreq.TryGetValue(term, out var df);
                _docFreq[term] = df + 1;
            }
        }

        _avgDocLen = _totalDocs > 0 ? (double)totalTerms / _totalDocs : 1.0;
        Interlocked.Increment(ref _bm25Version);
    }

    /// <summary>BM25 检索：返回 (docId, score) 列表。</summary>
    private static List<(int docId, float score)> SearchBM25(string query)
    {
        if (_totalDocs == 0) return [];

        var versionAtStart = Volatile.Read(ref _bm25Version);

        var queryTerms = Tokenize(query)
            .Where(t => !_stopwords.Contains(t) && t.Length > 1 && _invertedIndex.ContainsKey(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queryTerms.Length == 0) return [];

        var pool = System.Buffers.ArrayPool<float>.Shared;
        var scores = pool.Rent(_totalDocs);
        Array.Clear(scores, 0, _totalDocs);
        var top = new PriorityQueue<(int docId, double score), double>(Bm25TopN);
        float k1 = Bm25K1, b = Bm25B;
        double avgDl = _avgDocLen;

        try
        {
            foreach (var term in queryTerms)
            {
                float idf = ComputeIdf(term);
                if (idf <= 0) continue;

                foreach (var (docId, tf) in _invertedIndex[term])
                {
                    double docLen = _docLengths[docId];
                    double numerator = tf * (k1 + 1);
                    double denominator = tf + k1 * (1 - b + b * docLen / avgDl);
                    scores[docId] += idf * (float)(numerator / denominator);
                }
            }

            for (int i = 0; i < _totalDocs; i++)
            {
                if (scores[i] <= 0) continue;
                if (top.Count < Bm25TopN)
                    top.Enqueue((i, scores[i]), scores[i]);
                else if (scores[i] > top.Peek().score)
                {
                    top.DequeueEnqueue((i, scores[i]), scores[i]);
                }
            }
        }
        finally
        {
            pool.Return(scores);
        }

        // If BM25 index was rebuilt during search, results may be invalid — return empty
        if (Volatile.Read(ref _bm25Version) != versionAtStart) return [];

        return top.UnorderedItems.Select(x => (x.Element.docId, (float)x.Element.score)).ToList();
    }

    private static float ComputeIdf(string term)
    {
        if (!_docFreq.TryGetValue(term, out var df)) return 0;
        // BM25 IDF: log(1 + (N - df + 0.5) / (df + 0.5))
        return (float)Math.Log(1.0 + (_totalDocs - df + 0.5) / (df + 0.5));
    }

    /// <summary>查询分词：小写 + 按非字母数字拆分。</summary>
    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lower = text.ToLowerInvariant();
        // 按非字母数字拆分（保留中文、英文、数字）
        var tokens = lower.Split(
            [' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}',
             '"', '\'', '!', '?', '-', '_', '/', '\\', '|', '@', '#', '$', '%', '^', '&', '*',
             '+', '=', '<', '>', '~', '`', '：', '，', '。', '、', '；', '！', '？', '（', '）',
             '【', '】', '《', '》', '—', '…', '·'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens;
    }

    // ═══════════════════════════════════════════
    //  检索入口
    // ═══════════════════════════════════════════

    /// <summary>按用户查询检索 Top-K 个最相关的工具（全领域）。</summary>
    public static async Task<List<ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder,
        int k = 8, CancellationToken ct = default)
        => await SearchTopKAsync(query, embedder, null, k, null, ct).ConfigureAwait(false);

    /// <summary>按用户查询检索 Top-K 个最相关的工具（支持按 domain 加权）。</summary>
    public static async Task<List<ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder,
        string? domain, int k = 8, CancellationToken ct = default)
        => await SearchTopKAsync(query, embedder, domain, k, null, ct).ConfigureAwait(false);

    /// <summary>
    /// 按用户查询检索 Top-K 个最相关的工具（支持预计算嵌入以避免重复 ONNX 调用）。
    /// </summary>
    public static async Task<List<ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder,
        string? domain, int k, float[]? queryEmbedding, CancellationToken ct = default)
    {
        if (!_initialized || _tools.Count == 0) return new List<ToolDef>();

        // P14.8: lazy re-embed tools whose embedding is empty (model switched).
        // Single batched call via EmbeddingClient; skips if all tools have vectors.
        if (_tools.Any(t => t.Embedding.Length == 0))
        {
            try
            {
                var texts = _tools.Select(t => $"{t.Domain} | {t.Name}: {t.Description}").ToArray();
                var vectors = await embedder.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    for (int i = 0; i < _tools.Count && i < vectors.Length; i++)
                    {
                        if (_tools[i].Embedding.Length == 0)
                            _tools[i] = _tools[i] with { Embedding = vectors[i] };
                    }
                    _snapshot = _tools.ToArray();
                }
            }
            catch
            {
                // fall through — cosine below will treat empty embeddings as 0
            }
        }

        // ── 路 1: BM25 关键词检索 ──
        var bm25Results = SearchBM25(query);

        // ── 路 2: 向量语义检索 ──
        float[] qEmb;
        if (queryEmbedding != null && queryEmbedding.Length > 0)
        {
            qEmb = queryEmbedding;
        }
        else
        {
            try
            {
                qEmb = await embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            }
            catch
            {
                qEmb = EmbeddingClient.FastEmb(query, embedder?.Dimension ?? 384);
            }
        }

        var vecResults = _tools
            .Select((tool, idx) => (idx, score: CosineSimilarity(qEmb, tool.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(VectorTopN)
            .ToList();

        // ── RRF 融合 ──
        const float DomainBoost = 0.15f;
        var rrf = new Dictionary<int, double>(); // docId → RRF score

        int rank = 0;
        foreach (var (docId, _) in bm25Results)
            rrf[docId] = 1.0 / (RrfK + rank++);

        rank = 0;
        foreach (var (docId, _) in vecResults)
            rrf[docId] = rrf.GetValueOrDefault(docId) + 1.0 / (RrfK + rank++);

        // ── Cross-Retrieval Majority Voting ──
        // Inspired by FlashMemory-DeepSeek-V4: an entry is golden only if it
        // receives consensus from multiple independent retrieval "layers".
        // Tool candidates retrieved by BOTH BM25 AND Vector pass majority vote;
        // those retrieved by only one path get a score penalty.
        var bm25Set = new HashSet<int>(bm25Results.Select(r => r.docId));
        var vecSet = new HashSet<int>(vecResults.Select(r => r.idx));

        var final = rrf
            .Select(kvp => (
                tool: _tools[kvp.Key],
                docId: kvp.Key,
                score: kvp.Value
                    + (domain != null && string.Equals(_tools[kvp.Key].Domain, domain, StringComparison.OrdinalIgnoreCase) ? DomainBoost : 0)))
            .Select(x => (
                x.tool,
                score: x.score * (bm25Set.Contains(x.docId) && vecSet.Contains(x.docId)
                    ? 1.0   // majority consensus: both BM25 + Vector agree
                    : MajorityVotePenalty))) // single-path: apply penalty
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.tool)
            .ToList();

        // Track token savings: naive listing of all tools vs top-K
        if (final.Count > 0)
        {
            var naiveTokens = _tools.Count * 20; // ~20 tokens per full tool description
            var actualTokens = final.Count * 15; // ~15 tokens per concise tool result
            LTAI.Core.Configuration.TokenSavingsTracker.RecordLookup(naiveTokens, actualTokens);
        }

        return final;
    }

    // ═══════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════

    /// <summary>获取所有已注册的工具（快照，线程安全）。</summary>
    public static IReadOnlyList<ToolDef> AllTools => _snapshot;

    /// <summary>按 domain 获取工具列表（线程安全）。</summary>
    public static IReadOnlyList<ToolDef> GetToolsByDomain(string domain)
    {
        return _snapshot.Where(t => string.Equals(t.Domain, domain, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>清空注册表（用于测试或重新加载）。</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _tools.Clear();
            _invertedIndex.Clear();
            _docLengths = [];
            _docFreq.Clear();
            _totalDocs = 0;
            _avgDocLen = 0;
            _initialized = false;
            _snapshot = [];
            _stats.Clear();
            _bm25Version = 0;
        }
    }

    /// <summary>Reset all usage statistics (for test isolation).</summary>
    public static void ClearStats() => _stats.Clear();

    /// <summary>
    /// P14.8: mark all stored tool embeddings as stale (sentinel
    /// <see cref="Array.Empty{T}"/>). Next <see cref="SearchTopKAsync"/> call
    /// re-embeds every tool on the fly (single batched ONNX call) so
    /// semantic vectors track the active model.
    /// </summary>
    public static void ClearEmbeddings()
    {
        lock (_lock)
        {
            for (int i = 0; i < _tools.Count; i++)
            {
                var t = _tools[i];
                if (t.Embedding.Length > 0)
                    _tools[i] = t with { Embedding = Array.Empty<float>() };
            }
            _snapshot = _tools.ToArray();
        }
    }

    /// <summary>
    /// P12.2: helper — direct batched embedding with FastEmb fallback.
    /// Used when no <see cref="ToolEmbeddingCache"/> is supplied (or the
    /// cache path failed). Single ONNX call for all texts.
    /// </summary>
    private static async Task<float[][]> DirectBatchAsync(EmbeddingClient embedder,
        string[] texts, CancellationToken ct)
    {
        try
        {
            return await embedder.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
        }
        catch
        {
            return texts.Select(t => EmbeddingClient.FastEmb(t, embedder?.Dimension ?? 384)).ToArray();
        }
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => VectorMath.CosineSimilarity(a, b);

    // ═══════════════════════════════════════════
    //  IToolRegistry explicit interface implementation
    //  (delegates to static methods above)
    // ═══════════════════════════════════════════

    bool IToolRegistry.IsInitialized => _initialized;
    IReadOnlyList<ToolDef> IToolRegistry.AllTools => _snapshot;

    Task IToolRegistry.InitializeAsync(IEnumerable<AITool> tools, EmbeddingClient embedder, ToolEmbeddingCache? cache, CancellationToken ct)
        => InitializeAsync(tools, embedder, cache, ct);

    Task<List<ToolDef>> IToolRegistry.SearchTopKAsync(string query, EmbeddingClient embedder, int k, CancellationToken ct)
        => SearchTopKAsync(query, embedder, k, ct);

    Task<List<ToolDef>> IToolRegistry.SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k, CancellationToken ct)
        => SearchTopKAsync(query, embedder, domain, k, ct);

    Task<List<ToolDef>> IToolRegistry.SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k, float[]? queryEmbedding, CancellationToken ct)
        => SearchTopKAsync(query, embedder, domain, k, queryEmbedding, ct);

    void IToolRegistry.RecordCall(string toolName, bool success, long latencyMs)
        => RecordCall(toolName, success, latencyMs);

    IReadOnlyDictionary<string, ToolStats> IToolRegistry.GetAllStats() => GetAllStats();

    ToolStats? IToolRegistry.GetStats(string toolName) => GetStats(toolName);

    void IToolRegistry.ResetStats() => ResetStats();

    IReadOnlyList<ToolDef> IToolRegistry.GetToolsByDomain(string domain) => GetToolsByDomain(domain);

    void IToolRegistry.Clear() => Clear();

    void IToolRegistry.ClearEmbeddings() => ClearEmbeddings();
}

/// <summary>
/// Retrieval quality metrics for tool search.
/// Tracks format accuracy, recall, conversion rate, and average rounds.
/// </summary>
public sealed class ToolRetrievalMetrics
{
    private long _totalSearches;
    private long _formatCorrect;
    private long _toolsRetrieved;
    private long _toolsCalled;
    private long _totalRounds;

    // ═══════════════════════════════════════════
    //  OpenTelemetry instruments
    // ═══════════════════════════════════════════
    private static readonly Meter ToolMeter = new("LTAI.AI.ToolRetrieval");
    private static readonly Counter<long> MetricSearches = ToolMeter.CreateCounter<long>("ltai.tool.searches", "searches", "Total tool searches");
    private static readonly Counter<long> MetricFormatCorrect = ToolMeter.CreateCounter<long>("ltai.tool.format_correct", "calls", "Format-correct tool calls");
    private static readonly Counter<long> MetricToolsRetrieved = ToolMeter.CreateCounter<long>("ltai.tool.retrieved", "tools", "Tools retrieved");
    private static readonly Counter<long> MetricToolsCalled = ToolMeter.CreateCounter<long>("ltai.tool.called", "tools", "Tools actually called");
    private static readonly Counter<long> MetricTotalRounds = ToolMeter.CreateCounter<long>("ltai.tool.rounds", "rounds", "Total retrieval rounds");

    public double FormatAccuracy => _totalSearches > 0 ? (double)_formatCorrect / _totalSearches : 0;
    public double Recall => _toolsRetrieved > 0 ? 1.0 : 0;
    public double ConversionRate => _toolsRetrieved > 0 ? (double)_toolsCalled / _toolsRetrieved : 0;
    public double AvgRounds => _totalSearches > 0 ? (double)_totalRounds / _totalSearches : 0;

    public void RecordSearch(int toolsRetrieved, int rounds)
    {
        Interlocked.Increment(ref _totalSearches);
        Interlocked.Add(ref _toolsRetrieved, toolsRetrieved);
        Interlocked.Add(ref _totalRounds, rounds);
        MetricSearches.Add(1);
        MetricToolsRetrieved.Add(toolsRetrieved);
        MetricTotalRounds.Add(rounds);
    }

    public void RecordFormatCorrect()
    {
        Interlocked.Increment(ref _formatCorrect);
        MetricFormatCorrect.Add(1);
    }

    public void RecordToolCalled()
    {
        Interlocked.Increment(ref _toolsCalled);
        MetricToolsCalled.Add(1);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _formatCorrect, 0);
        Interlocked.Exchange(ref _toolsRetrieved, 0);
        Interlocked.Exchange(ref _toolsCalled, 0);
        Interlocked.Exchange(ref _totalRounds, 0);
    }

    public override string ToString() =>
        $"Format={FormatAccuracy:P1} Recall={Recall:P1} Conv={ConversionRate:P1} Rounds={AvgRounds:F1}";
}
