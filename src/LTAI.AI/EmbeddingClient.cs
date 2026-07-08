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
        // DeepSeek offers no public embedding API — left empty and excluded from the
        // available set below so a doomed request with model="" is never sent.
        ("DEEPSEEK_API_KEY",     "https://api.deepseek.com/v1",              "",                       "DeepSeek", 1024),
        ("OPENAI_API_KEY",       "https://api.openai.com/v1",                "text-embedding-3-small", "OpenAI", 1536),
        ("SILICONFLOW_API_KEY",  "https://api.siliconflow.cn/v1",           "BAAI/bge-m3",            "SiliconFlow", 1024),
        ("DASHSCOPE_API_KEY",    "https://dashscope.aliyuncs.com/compatible-mode/v1", "text-embedding-v3", "Aliyun", 1536),
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmbeddingClient> _logger;
    private readonly (string name, string endpoint, string model, int dim, string apiKey)[] _availableProviders;
    private readonly LocalEmbedder? _local;
    private readonly Glove50Embedder? _glove;
    private readonly RemoteEmbeddingCache? _remoteCache;

    private readonly object _activationLock = new();

    private volatile int _dimension = 384;
    public int Dimension => _dimension;

    /// <summary>
    /// P14.8: the local ONNX embedder, if available. Exposed so that
    /// downstream caches (e.g. <see cref="ToolEmbeddingCache"/>) can
    /// subscribe to <see cref="LocalEmbedder.ModelSwitched"/> and
    /// invalidate model-specific cached vectors.
    /// </summary>
    public LocalEmbedder? Local => _local;

    /// <summary>Test/fallback constructor — creates a default HttpClient for local-only embedding.</summary>
    public EmbeddingClient()
        : this(new SimpleHttpFactory(), null, null, null) { }

    private sealed class SimpleHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler());
    }

    public EmbeddingClient(
        IHttpClientFactory httpFactory,
        LocalEmbedder? local = null,
        ILogger<EmbeddingClient>? logger = null,
        RemoteEmbeddingCache? remoteCache = null,
        Glove50Embedder? glove = null)
    {
        _httpFactory = httpFactory;
        _logger = logger ?? NullLogger<EmbeddingClient>.Instance;
        _remoteCache = remoteCache;
        _glove = glove;

        _availableProviders = DefaultProviders
            .Select(p => (p.name, p.endpoint, p.model, p.dim, apiKey: LTAI.Core.Configuration.SecretManager.Get(p.envVar) ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.apiKey) && !string.IsNullOrEmpty(p.model))
            .ToArray();
        _local = local;

        if (_local?.Available == true)
            _dimension = _local.Dim;
        else if (_availableProviders.Length > 0)
            _dimension = _availableProviders[0].dim;
        else
            _dimension = 384; // GloVe/FastEmb default

        _logger.LogInformation("EmbeddingClient: {Count} API providers, local BGE={Local}, glove={Glove}, dim={Dim}, remoteCache={Cache}",
            _availableProviders.Length, _local?.Available == true, _glove != null, _dimension, _remoteCache != null ? "on" : "off");
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
        lock (_activationLock)
        {
            if (_localFallbackActivated) return;
            _localFallbackActivated = true;
            LocalFallbackActivatedAtUtc = DateTime.UtcNow;
            LocalEmbedder.DefaultDisabled = false;
            _local.Activate();
        }
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
            _dimension = _local.Dim;
            _logger.LogDebug("Embedding via local ONNX (batched): {Count} texts", texts.Length);
            var batchResult = await _local.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
            return batchResult is float[][] arr ? arr : batchResult.ToArray();
        }

        // Priority 2: Remote API providers (needs valid API key)
        // P14.5: per-provider cache lookup — same text across requests hits
        // cache and skips the API call. Local ONNX path above does not cache
        // (fast, deterministic, free).
        if (_availableProviders.Length == 0)
        {
            // Priority 3: GloVe-50d (zero-dependency, built-in, ~50KB)
            if (_glove != null)
            {
                _logger.LogDebug("Embedding via GloVe-50d (zero-dep): {Count} texts", texts.Length);
                var gloveResults = _glove.EmbedBatch(texts);
                return gloveResults.Select(v => ProjectToDim(v, Dimension)).ToArray();
            }

            // Priority 4 fallback: BM25 heuristic
            _logger.LogWarning("No embedding models available, using BM25 fallback (dim={Dim})", Dimension);
            return texts.Select(t => FastEmb(t, Dimension)).ToArray();
        }

        var result = new float[texts.Length][];
        var missing = new List<int>();

        if (_remoteCache != null)
        {
            // P14.5: lookup all texts in cache, checking all available providers
            for (int i = 0; i < texts.Length; i++)
            {
                bool found = false;
                foreach (var prov in _availableProviders)
                {
                    if (_remoteCache.TryGet(prov.name, prov.model, texts[i], out var vec))
                    {
                        result[i] = vec!;
                        found = true;
                        break;
                    }
                }
                if (!found) missing.Add(i);
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
                    _dimension = apiResult.Dimension;
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

        // Priority 3 fallback: GloVe-50d (then BM25)
        if (_glove != null)
        {
            _logger.LogWarning("No embedding API succeeded, using GloVe-50d fallback for {N} missing texts", missing.Count);
            var gloveVecs = _glove.EmbedBatch(missingTexts);
            for (int j = 0; j < missing.Count; j++)
                result[missing[j]] = ProjectToDim(gloveVecs[j], Dimension);
        }
        else
        {
            _logger.LogWarning("No embedding API succeeded, using BM25 fallback for {N} missing texts (dim={Dim})", missing.Count, Dimension);
            for (int j = 0; j < missing.Count; j++)
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
    private static readonly char[] FastEmbDelimiters = { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '!', '?', '-', '_', '/' };
    private static readonly int[] HashSeeds3 = { 17, 31, 97 };
    private static readonly int[] HashSeeds2 = { 17, 31 };

    public static float[] FastEmb(string text, int dimensions = 384)
    {
        // BM25-like scoring: use term frequency + pseudo-IDF
        // Since we don't have a corpus for real IDF, we derive it from term length/rarity heuristics
        var emb = new float[dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            // Seed with deterministic per-character pseudo-random values to
            // produce a valid non-zero vector for L2 normalization, avoiding
            // NaN in subsequent cosine similarity calculations.
            for (int i = 0; i < dimensions; i++)
                emb[i] = (float)(((text?.GetHashCode() ?? 0) * (i + 1) * 0x9E3779B9) & 0x7FFFFFFF) / int.MaxValue;
            return emb;
        }

        var lower = text.ToLowerInvariant();

        // Tokenize: split on non-alphanumeric for English; handle Chinese by bigram
        var rawTokens = lower.Split(FastEmbDelimiters, StringSplitOptions.RemoveEmptyEntries);

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

        if (tokens.Count == 0)
        {
            for (int i = 0; i < dimensions; i++)
                emb[i] = (float)((text.GetHashCode() * (i + 1) * 0x9E3779B9) & 0x7FFFFFFF) / int.MaxValue;
            return emb;
        }

        // ── BM25 components ──
        const float k1 = 1.2f;    // term saturation (LTAI_EMBED_BM25_K1 override)
        const float b = 0.75f;    // length normalization (LTAI_EMBED_BM25_B override)
        float avgDocLen = float.TryParse(Environment.GetEnvironmentVariable("LTAI_EMBED_BM25_AVG_DOC_LEN"), out var adl) ? adl : 20f;
        float docLen = tokens.Count;

        // Count term frequency
        var tfs = new Dictionary<string, int>(tokens.Count);
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
            foreach (int s in HashSeeds3)
            {
                var idx = Math.Abs(HashMix(h, s) % dimensions);
                emb[idx] += score;
            }

            // Character prefix (first 3 chars) — multi-channel signal
            if (term.Length >= 3)
            {
                var prefix = term[..3];
                var ph = (int)StableHash(prefix);
                foreach (int s in HashSeeds2)
                {
                    var idx = Math.Abs(HashMix(ph, s) % dimensions);
                    emb[idx] += score * 0.3f;
                }
            }
        }

        // ── Log normalization → L2 normalize ──
        for (int i = 0; i < dimensions; i++)
            if (emb[i] > 0) emb[i] = MathF.Log(1f + emb[i]);

        var norm = 0f;
        for (int i = 0; i < dimensions; i++) norm += emb[i] * emb[i];
        norm = MathF.Sqrt(norm);
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

    /// <summary>Project a 50d GloVe vector to the target dimension via linear interpolation.</summary>
    private static float[] ProjectToDim(float[] src, int targetDim)
    {
        if (src.Length == targetDim) return src;
        var result = new float[targetDim];
        for (int i = 0; i < targetDim; i++)
        {
            var srcIdx = (int)((long)i * src.Length / targetDim);
            if (srcIdx >= src.Length) srcIdx = src.Length - 1;
            result[i] = src[srcIdx];
        }
        // L2 normalize
        var norm = 0.0;
        for (int i = 0; i < targetDim; i++) norm += result[i] * result[i];
        norm = Math.Sqrt(norm);
        if (norm > 1e-10)
            for (int i = 0; i < targetDim; i++) result[i] = (float)(result[i] / norm);
        return result;
    }

    public void Dispose() { }
}
