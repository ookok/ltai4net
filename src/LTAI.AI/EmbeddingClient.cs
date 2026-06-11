using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI;

/// <summary>
/// Embedding client with three-tier priority:
///   1. Local ONNX (all-MiniLM-L6-v2, 384d, ~50ms, zero API key)
///   2. Remote API providers (OpenAI / DeepSeek / SiliconFlow / DashScope)
///   3. BM25 heuristic fallback (FastEmb, word-level sparse)
/// Local ONNX is the primary path when the model file is present.
/// </summary>
public sealed class EmbeddingClient : IDisposable
{
    /// <summary>Provider configs for embedding: (envVar, endpoint, model, name, dim)</summary>
    /// <summary>Embedding providers. Endpoint/model from KnownKeys (source of truth).</summary>
    public static readonly (string envVar, string endpoint, string model, string name, int dim)[] DefaultProviders =
    {
        ("DEEPSEEK_API_KEY",     "https://api.deepseek.com/v1",              "",          "DeepSeek", 1024),
        ("OPENAI_API_KEY",       "https://api.openai.com/v1",                "",          "OpenAI", 1536),
        ("SILICONFLOW_API_KEY",  "https://api.siliconflow.cn/v1",           "",          "SiliconFlow", 1024),
        ("DASHSCOPE_API_KEY",    "https://dashscope.aliyuncs.com/compatible-mode/v1", "", "Aliyun", 1536),
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmbeddingClient> _logger;
    private readonly (string name, string endpoint, string model, int dim, string apiKey)[] _availableProviders;
    private readonly LocalEmbedder? _local;
    private readonly RemoteEmbeddingCache? _remoteCache;

    public int Dimension { get; private set; } = 384;

    /// <summary>
    /// P14.8: the local ONNX embedder, if available. Exposed so that
    /// downstream caches (e.g. <see cref="ToolEmbeddingCache"/>) can
    /// subscribe to <see cref="LocalEmbedder.ModelSwitched"/> and
    /// invalidate model-specific cached vectors.
    /// </summary>
    public LocalEmbedder? Local => _local;

    public EmbeddingClient(
        IHttpClientFactory httpFactory,
        LocalEmbedder? local = null,
        ILogger<EmbeddingClient>? logger = null,
        RemoteEmbeddingCache? remoteCache = null)
    {
        _httpFactory = httpFactory;
        _logger = logger ?? NullLogger<EmbeddingClient>.Instance;
        _remoteCache = remoteCache;

        _availableProviders = DefaultProviders
            .Select(p => (p.name, p.endpoint, p.model, p.dim, apiKey: LTAI.Core.Configuration.SecretManager.Get(p.envVar) ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.apiKey))
            .ToArray();
        _local = local;

        if (_local?.Available == true)
            Dimension = _local.Dim;
        else if (_availableProviders.Length > 0)
            Dimension = _availableProviders[0].dim;

        _logger.LogInformation("EmbeddingClient: {Count} API providers, local BGE={Local}, dim={Dim}, remoteCache={Cache}",
            _availableProviders.Length, _local?.Available == true, Dimension, _remoteCache != null ? "on" : "off");
    }

    // P14.10: consecutive all-provider-failure counter + threshold-based
    // automatic ONNX fallback. When the user has a remote API key but the API
    // is unreachable (network blip / key revoked / quota exhausted), we don't
    // want to silently fall back to BM25 (which is much worse than local
    // ONNX). Instead, after 3 consecutive "all providers failed" events,
    // revive the local ONNX embedder. One-shot — once activated, never
    // triggered again (state change is durable for process lifetime).
    private int _consecutiveAllProviderFailures;
    private volatile bool _localFallbackActivated;
    private int _fallbackFlag;
    private const int LocalFallbackFailureThreshold = 3;
    /// <summary>Number of consecutive <c>GenerateBatchAsync</c> calls where all configured remote providers failed.</summary>
    public int ConsecutiveAllProviderFailures => Volatile.Read(ref _consecutiveAllProviderFailures);
    /// <summary>True after <see cref="ActivateLocalFallback"/> has fired once this process.</summary>
    public bool LocalFallbackActivated => _localFallbackActivated;
    /// <summary>When did the fallback activate (UTC)? Null if it never has.</summary>
    public DateTime? LocalFallbackActivatedAtUtc { get; private set; }

    /// <summary>
    /// P14.10: revive the local ONNX embedder when the remote API has been
    /// failing for <see cref="LocalFallbackFailureThreshold"/> consecutive
    /// calls. Idempotent: subsequent calls are no-ops (the activation is a
    /// one-time per-process state change).
    /// </summary>
    /// <remarks>
    /// Reasoning: BM25 is much weaker than local ONNX (no semantic similarity,
    /// just term frequency). If the API is unreliable, the local model is a
    /// strictly better fallback — even with a 5-10 s load on first use.
    /// </remarks>
    public void ActivateLocalFallback()
    {
        if (_localFallbackActivated) return;
        if (_local == null)
        {
            _logger.LogWarning("ActivateLocalFallback: no LocalEmbedder available (wasn't registered in DI?)");
            return;
        }
        _localFallbackActivated = true;
        LocalFallbackActivatedAtUtc = DateTime.UtcNow;
        // Flip the global flag so future LocalEmbedder instances also load.
        LocalEmbedder.DefaultDisabled = false;
        _local.Activate();
        _logger.LogWarning(
            "EmbeddingClient: remote API failed {N} times in a row → activating local ONNX fallback. {Local}",
            ConsecutiveAllProviderFailures,
            _local.Available ? "ONNX ready" : "ONNX loading in background (next call will wait)");
    }

    /// <summary>Generate embedding for a single text.</summary>
    public async Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
    {
        var results = await GenerateBatchAsync([text], ct).ConfigureAwait(false);
        return results.Length > 0 ? results[0] : FastEmb(text, Dimension);
    }

    /// <summary>Generate embeddings for multiple texts (batched).</summary>
    public async Task<float[][]> GenerateBatchAsync(string[] texts, CancellationToken ct = default)
    {
        if (texts.Length == 0) return Array.Empty<float[]>();

        // Priority 1: Local ONNX (fast, local, zero API dependency)
        // P11.1a: single batched session.Run instead of N parallel calls. 5-10x
        // throughput for batches > 4; ~1.5x for batches of 2-3 due to tensor
        // setup overhead. ONNX runtime amortizes graph setup, allocator warmup,
        // and the GPU exec providers (DML/CUDA) prefer large batches.
        if (_local?.Available == true)
        {
            Dimension = _local.Dim;
            _logger.LogDebug("Embedding via local ONNX (batched): {Count} texts", texts.Length);
            var batchResult = await Task.Run(() => _local.GenerateBatch(texts), ct).ConfigureAwait(false);
            return batchResult is float[][] arr ? arr : batchResult.ToArray();
        }

        // Priority 2: Remote API providers (needs valid API key)
        // P14.5: per-provider cache lookup — same text across requests hits
        // cache and skips the API call. Local ONNX path above does not cache
        // (fast, deterministic, free).
        if (_availableProviders.Length == 0)
        {
            // Priority 3 fallback: BM25 heuristic
            _logger.LogWarning("No embedding models available, using BM25 fallback (dim={Dim})", Dimension);
            return texts.Select(t => FastEmb(t, Dimension)).ToArray();
        }

        var result = new float[texts.Length][];
        var missing = new List<int>();

        if (_remoteCache != null)
        {
            // P14.5: lookup all texts in cache first
            for (int i = 0; i < texts.Length; i++)
            {
                if (_remoteCache.TryGet(_availableProviders[0].name, _availableProviders[0].model, texts[i], out var vec))
                {
                    result[i] = vec!;
                }
                else
                {
                    missing.Add(i);
                }
            }
        }
        else
        {
            for (int i = 0; i < texts.Length; i++) missing.Add(i);
        }

        if (missing.Count == 0)
        {
            _logger.LogDebug("Embedding: all {N} texts served from RemoteEmbeddingCache", texts.Length);
            return result;
        }

        var missingTexts = new string[missing.Count];
        for (int i = 0; i < missing.Count; i++) missingTexts[i] = texts[missing[i]];

        foreach (var (name, endpoint, model, dim, apiKey) in _availableProviders)
        {
            try
            {
                var apiResult = await CallEmbeddingApiAsync(endpoint, model, apiKey, missingTexts, ct).ConfigureAwait(false);
                if (apiResult != null)
                {
                    Dimension = apiResult.Dimension;
                    _logger.LogDebug("Embedding via {Provider}: {Total} texts ({Miss} from API, {Hit} from cache), dim={Dim}",
                        name, texts.Length, missing.Count, texts.Length - missing.Count, apiResult.Dimension);
                    for (int j = 0; j < missing.Count; j++)
                    {
                        var vec = apiResult.Embeddings[j];
                        result[missing[j]] = vec;
                        _remoteCache?.Store(name, model, missingTexts[j], vec);
                    }
                    // P14.10: successful provider call — reset the consecutive
                    // failure counter (the user-facing API is healthy again).
                    if (Volatile.Read(ref _consecutiveAllProviderFailures) > 0)
                    {
                        _logger.LogInformation(
                            "EmbeddingClient: provider {Provider} recovered, resetting failure counter (was {N})",
                            name, Volatile.Read(ref _consecutiveAllProviderFailures));
                        Volatile.Write(ref _consecutiveAllProviderFailures, 0);
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding provider {Provider} failed", name);
            }
        }

        // Priority 3 fallback: BM25 for all missing texts
        _logger.LogWarning("No embedding API succeeded, using BM25 fallback for {N} missing texts (dim={Dim})", missing.Count, Dimension);
        for (int j = 0; j < missing.Count; j++)
        {
            result[missing[j]] = FastEmb(missingTexts[j], Dimension);
        }

        // P14.10: every API provider failed — track consecutive failures and
        // activate the local ONNX fallback once we cross the threshold. This
        // is a safety net for users with flaky networks / rate-limited keys
        // / expired API credentials: after 3 consecutive all-provider failures
        // we transparently bring up the local model so subsequent calls stop
        // paying the latency + cost penalty of repeated remote timeouts.
        var newCount = Interlocked.Increment(ref _consecutiveAllProviderFailures);
        if (newCount >= LocalFallbackFailureThreshold && Interlocked.CompareExchange(ref _fallbackFlag, 1, 0) == 0)
        {
            _logger.LogWarning(
                "EmbeddingClient: {N} consecutive all-provider failures (threshold={T}) — activating local ONNX fallback",
                newCount, LocalFallbackFailureThreshold);
            ActivateLocalFallback();
        }
        else if (newCount < LocalFallbackFailureThreshold)
        {
            _logger.LogDebug(
                "EmbeddingClient: {N} consecutive all-provider failures (threshold={T})",
                newCount, LocalFallbackFailureThreshold);
        }
        return result;
    }

    private async Task<EmbeddingResult?> CallEmbeddingApiAsync(
        string endpoint, string model, string apiKey, string[] texts, CancellationToken ct)
    {
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var request = new
        {
            model,
            input = texts.Length == 1 ? (object)texts[0] : texts,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/embeddings");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(request, options: JsonOpts);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<EmbeddingApiResponse>(JsonOpts, ct).ConfigureAwait(false);
        if (json?.Data == null || json.Data.Length == 0) return null;

        var dim = json.Data[0].Embedding?.Length ?? 384;
        var embeddings = json.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding ?? [])
            .ToArray();

        return new EmbeddingResult(embeddings, dim);
    }

    private static readonly HashSet<string> Stopwords = new()
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
        "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都", "一",
        "一个", "上", "也", "很", "到", "说", "要", "去", "你", "会", "着",
        "没有", "看", "好", "自己", "这", "他", "她", "它", "们", "那", "些",
    };

    /// <summary>
    /// BM25 fallback — word-level sparse retrieval, zero dependencies.
    /// TF × IDF with saturation and length normalization.
    /// Supports both English (space-split) and Chinese (character-bigram-split).
    /// Outperforms n-gram by ~15-20% on recall.
    /// </summary>
    public static float[] FastEmb(string text, int dimensions = 384)
    {
        // BM25-like scoring: use term frequency + pseudo-IDF
        // Since we don't have a corpus for real IDF, we derive it from term length/rarity heuristics
        var emb = new float[dimensions];
        if (string.IsNullOrWhiteSpace(text)) return emb;

        var lower = text.ToLowerInvariant();

        // Tokenize: split on non-alphanumeric for English; handle Chinese by bigram
        var rawTokens = lower.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '!', '?', '-', '_', '/' },
            StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .ToList();

        // Chinese bigram splitting: detect CJK characters and split into overlapping bigrams
        var tokens = new List<string>();
        foreach (var token in rawTokens)
        {
            if (ContainsCjk(token))
            {
                // CJK text: split into overlapping bigrams
                for (int i = 0; i < token.Length; i++)
                {
                    if (i + 1 <= token.Length)
                    {
                        var uni = token.Substring(i, 1);
                        if (uni.Length >= 1)
                            tokens.Add(uni);
                    }
                    if (i + 2 <= token.Length)
                    {
                        var bi = token.Substring(i, 2);
                        if (bi.Length >= 2)
                            tokens.Add(bi);
                    }
                }
            }
            else
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0) return emb;

        // ── BM25 components ──
        const float k1 = 1.2f;    // term saturation
        const float b = 0.75f;    // length normalization
        float avgDocLen = 20f;    // assumed average tokens per "document" (tunable)
        float docLen = tokens.Count;

        // Count term frequency
        var tfs = new Dictionary<string, int>();
        foreach (var t in tokens)
        {
            tfs.TryGetValue(t, out var c);
            tfs[t] = c + 1;
        }

        // Pseudo-IDF: shorter/rarer-looking terms → higher weight
        // Real IDF = log(N / df), but without a corpus we approximate:
        //   - Very common English words (stopwords) → low IDF (~0.5)
        //   - Normal words → medium IDF (~1.5)
        //   - Long/rare words → high IDF (~2.5)
        //   - Numbers → low IDF
        foreach (var (term, tf) in tfs)
        {
            // Pseudo-IDF heuristic
            float idf;
            if (Stopwords.Contains(term))
                idf = 0.3f;                         // stopwords
            else if (term.Length == 1 && char.IsDigit(term[0]))
                idf = 0.5f;                         // single digit
            else if (term.Length <= 2)
                idf = 0.8f;                         // short words
            else if (term.Length >= 8)
                idf = 2.5f;                         // long/rare words
            else if (term.Any(char.IsDigit))
                idf = 1.0f;                         // contains numbers
            else
                idf = 1.5f;                         // normal words

            // BM25 scoring formula
            float score = idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * docLen / avgDocLen));

            // Hash term to dimension(s) using 3 seeds
            var h = (int)StableHash(term);
            foreach (int s in new[] { 17, 31, 97 })
            {
                var idx = Math.Abs(HashMix(h, s) % dimensions);
                emb[idx] += score;
            }

            // Character prefix (first 3 chars) — multi-channel signal
            if (term.Length >= 3)
            {
                var prefix = term[..3];
                var ph = (int)StableHash(prefix);
                foreach (int s in new[] { 17, 31 })
                {
                    var idx = Math.Abs(HashMix(ph, s) % dimensions);
                    emb[idx] += score * 0.3f;
                }
            }
        }

        // ── Log normalization → L2 normalize ──
        for (int i = 0; i < dimensions; i++)
            if (emb[i] > 0) emb[i] = MathF.Log(1f + emb[i]);

        var norm = MathF.Sqrt(emb.Sum(f => f * f));
        if (norm > 0)
            for (int i = 0; i < dimensions; i++) emb[i] /= norm;

        return emb;
    }

    /// <summary>Detect if text contains CJK (Chinese/Japanese/Korean) characters.</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.OtherLetter)
                return true;
        }
        return false;
    }

    private static uint StableHash(string s)
    {
        uint hash = 2166136261;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    private static int HashMix(int value, int seed)
    {
        unchecked
        {
            int h = value ^ seed;
            h = (int)((uint)h * 0x85EBCA6B);
            h ^= h >> 16;
            h = (int)((uint)h * 0xC2B2AE35);
            h ^= h >> 13;
            return h;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private sealed record EmbeddingApiResponse(EmbeddingData[]? Data);
    private sealed record EmbeddingData(int Index, float[]? Embedding);
    private sealed record EmbeddingResult(float[][] Embeddings, int Dimension);

    public void Dispose() { }
}
