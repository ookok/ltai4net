// Tool RAG: 双路检索 + RRF 融合
// BM25（关键词匹配）+ Vector（语义相似度）→ RRF 融合排序
// 参照 KbGraph 的混合检索模式，k=60

using System;
using System.Collections.Generic;
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
/// </summary>
public static class ToolRegistry
{
    /// <summary>单个工具的定义 + embedding + domain。</summary>
    public sealed record ToolDef(string Name, string Description, float[] Embedding, string Domain = "");

    private static readonly List<ToolDef> _tools = new();
    private static volatile bool _initialized;
    private static readonly object _lock = new();

    // ═══════════════════════════════════════════
    //  BM25 倒排索引
    // ═══════════════════════════════════════════

    /// <summary>倒排索引: term → (docId, termFrequency) 列表</summary>
    private static Dictionary<string, List<(int docId, int tf)>> _invertedIndex = new();
    /// <summary>每个文档的长度（词数）</summary>
    private static int[] _docLengths = [];
    /// <summary>包含每个 term 的文档数</summary>
    private static Dictionary<string, int> _docFreq = new();
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
        catch { }
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
        catch { }
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
                        : EmbeddingClient.FastEmb(BuildEmbeddingText(t)))
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
            var emb = i < embeddings.Length ? embeddings[i] : EmbeddingClient.FastEmb(texts[i]);
            var domain = GetToolDomain(list[i]);
            defs.Add(new ToolDef(list[i].Name ?? "unknown", list[i].Description ?? "", emb, domain));
        }

        // ── BM25 倒排索引 ──
        lock (_lock)
        {
            if (_initialized) return;
            BuildBm25Index(texts);
            _tools.AddRange(defs);
            _initialized = true;
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
    }

    /// <summary>BM25 检索：返回 (docId, score) 列表。</summary>
    private static List<(int docId, float score)> SearchBM25(string query)
    {
        if (_totalDocs == 0) return [];

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
        => await SearchTopKAsync(query, embedder, null, k, ct).ConfigureAwait(false);

    /// <summary>按用户查询检索 Top-K 个最相关的工具（支持按 domain 加权）。</summary>
    public static async Task<List<ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder,
        string? domain, int k = 8, CancellationToken ct = default)
    {
        if (!_initialized || _tools.Count == 0) return new List<ToolDef>();

        // ── 路 1: BM25 关键词检索 ──
        var bm25Results = SearchBM25(query);

        // ── 路 2: 向量语义检索 ──
        float[] qEmb;
        try
        {
            qEmb = await embedder.GenerateAsync(query, ct).ConfigureAwait(false);
        }
        catch
        {
            qEmb = EmbeddingClient.FastEmb(query);
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

        // ── domain 加权 + Top-K ──
        var final = rrf
            .Select(kvp => (
                tool: _tools[kvp.Key],
                score: kvp.Value
                    + (domain != null && string.Equals(_tools[kvp.Key].Domain, domain, StringComparison.OrdinalIgnoreCase) ? DomainBoost : 0)))
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.tool)
            .ToList();

        return final;
    }

    // ═══════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════

    /// <summary>获取所有已注册的工具。</summary>
    public static IReadOnlyList<ToolDef> AllTools => _tools;

    /// <summary>按 domain 获取工具列表。</summary>
    public static IReadOnlyList<ToolDef> GetToolsByDomain(string domain)
        => _tools.Where(t => string.Equals(t.Domain, domain, StringComparison.OrdinalIgnoreCase)).ToList();

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
            return texts.Select(t => EmbeddingClient.FastEmb(t)).ToArray();
        }
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, normA = 0, normB = 0;
        int i = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= System.Numerics.Vector<float>.Count)
        {
            int vecLen = System.Numerics.Vector<float>.Count;
            var aVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(a);
            var bVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(b);
            var vdot = System.Numerics.Vector<float>.Zero;
            var vna = System.Numerics.Vector<float>.Zero;
            var vnb = System.Numerics.Vector<float>.Zero;
            for (int j = 0; j < aVecs.Length; j++)
            {
                vdot += aVecs[j] * bVecs[j];
                vna += aVecs[j] * aVecs[j];
                vnb += bVecs[j] * bVecs[j];
            }
            for (int k = 0; k < vecLen; k++)
            {
                dot += vdot[k];
                normA += vna[k];
                normB += vnb[k];
            }
            i += aVecs.Length * vecLen;
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }
}
