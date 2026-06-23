using System.Runtime.CompilerServices;
using LTAI.AI;
using LTAI.Agent.Experts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace LTAI.Agent.Clients;

/// <summary>
/// MAF-aligned <see cref="IChatClient"/> middleware that filters <see cref="ChatOptions.Tools"/>
/// via semantic retrieval before the LLM call.
///
/// Pipeline:
///   1. BM25 + ONNX embedding → RRF fusion → top-20 candidates (fast, zero-LLM, ~5ms)
///   2. ONNX MiniLM pseudo-cross-encoder re-rank → top-8 tools (local, ~100ms batch)
///   3. L3 LLM fallback if ONNX unavailable (remote, ~300ms)
/// </summary>
public sealed class ToolFilteringChatClient : IChatClient
{
    private static readonly HashSet<string> PinnedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFileContent", "RunCommand", "ListFiles", "GetCurrentDateTime",
    };

    private const int DefaultTopK = 8;
    private const int RerankCandidateN = 20;
    private const int MaxProactiveRounds = 3;
    private const int ToolsPerProactiveRound = 8;

    private readonly IChatClient _inner;
    private readonly EmbeddingClient _embedder;
    private readonly IToolRegistry _toolRegistry;
    private readonly ToolEmbeddingCache? _cache;
    private readonly QueryEmbeddingCache? _queryCache;
    private readonly IChatClient? _l3Client;
    private string? _lastQuery;
    private int _lastToolCount;
    private readonly MemoryCache _resultCache = new(new MemoryCacheOptions());
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>多轮主动检索统计: (query, rounds, toolsFound) 记录。</summary>
    public IReadOnlyList<(string Query, int Rounds, int ToolsFound)> ProactiveRetrievalHistory =>
        _proactiveHistory.AsReadOnly();
    private readonly List<(string, int, int)> _proactiveHistory = [];

    public ToolFilteringChatClient(IChatClient inner, EmbeddingClient embedder,
        IToolRegistry toolRegistry,
        ToolEmbeddingCache? cache = null, QueryEmbeddingCache? queryCache = null,
        IChatClient? l3Client = null)
    {
        _inner = inner;
        _embedder = embedder;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _queryCache = queryCache;
        _l3Client = l3Client;
    }

    public void Dispose() => _inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var filteredOptions = await FilterToolsAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return await _inner.GetResponseAsync(messages, filteredOptions, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filteredOptions = await FilterToolsAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, filteredOptions, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) return null;
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
    }

    private async ValueTask<ChatOptions?> FilterToolsAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        if (options?.Tools is null || options.Tools.Count == 0)
            return options;

        if (!_toolRegistry.IsInitialized)
        {
            await _toolRegistry.InitializeAsync(options.Tools, _embedder, _cache, ct).ConfigureAwait(false);
        }

        var query = GetLastUserQuery(messages);

        // Skip entire filter pipeline if query and tool count unchanged between turns
        if (!string.IsNullOrWhiteSpace(query) && query == _lastQuery && options.Tools.Count == _lastToolCount)
        {
            return options;
        }
        _lastQuery = query;
        _lastToolCount = options.Tools.Count;

        var tools = options.Tools.ToList();
        List<AITool> selectedTools;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var cacheKey = $"tf:{query}|{tools.Count}";
            if (_resultCache.TryGetValue(cacheKey, out ChatOptions? cachedOpts) && cachedOpts != null)
            {
                var cachedClone = cachedOpts.Clone();
                var cachedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in cachedOpts.Tools)
                    if (t.Name != null) cachedNames.Add(t.Name);
                var syncedTools = new List<AITool>(tools.Count);
                foreach (var t in tools)
                    if (cachedNames.Contains(t.Name ?? "") || PinnedTools.Contains(t.Name ?? ""))
                        syncedTools.Add(t);
                cachedClone.Tools = syncedTools;
                return cachedClone;
            }

            var queryEmb = _queryCache?.Get(query);
            // Stage 1: BM25 + ONNX bi-encoder → RRF fusion → top-20 candidates
            var hits = await _toolRegistry.SearchTopKAsync(query, _embedder, null,
                RerankCandidateN, queryEmb, ct).ConfigureAwait(false);

            // Stage 2: Re-rank — try local ONNX pseudo-cross-encoder first, fall back to L3 LLM
            if (hits.Count > DefaultTopK)
            {
                hits = await RerankAsync(query, hits, ct).ConfigureAwait(false);
            }

            var hitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int takeCount = Math.Min(hits.Count, DefaultTopK);
            for (int i = 0; i < takeCount; i++)
                hitNames.Add(hits[i].Name);

            selectedTools = new List<AITool>(DefaultTopK);
            foreach (var t in tools)
                if (hitNames.Contains(t.Name ?? "") || PinnedTools.Contains(t.Name ?? ""))
                    selectedTools.Add(t);
        }
        else
        {
            selectedTools = tools;
        }

        if (selectedTools.Count < 3)
        {
            var fallback = new List<AITool>(DefaultTopK);
            foreach (var t in tools)
                if (PinnedTools.Contains(t.Name ?? ""))
                    fallback.Add(t);
            foreach (var t in tools)
            {
                if (!PinnedTools.Contains(t.Name ?? "") && fallback.Count < DefaultTopK)
                    fallback.Add(t);
            }
            selectedTools = fallback;
        }

        var clone = options.Clone();
        clone.Tools = selectedTools;
        _resultCache.Set($"tf:{query}|{tools.Count}", clone, ResultCacheTtl);
        return clone;
    }

    /// <summary>
    /// Multi-round proactive tool retrieval (ToolOmni-inspired).
    /// Iteratively searches, evaluates sufficiency, and refines query
    /// until tool set is adequate or max rounds reached.
    /// </summary>
    public async Task<IReadOnlyList<ToolRegistry.ToolDef>> RetrieveToolsProactively(
        string userQuery,
        int maxRounds = MaxProactiveRounds,
        int toolsPerRound = ToolsPerProactiveRound,
        CancellationToken ct = default)
    {
        var collected = new Dictionary<string, ToolRegistry.ToolDef>(StringComparer.OrdinalIgnoreCase);
        var searchQuery = userQuery;

        for (int round = 0; round < maxRounds; round++)
        {
            var hits = await _toolRegistry.SearchTopKAsync(searchQuery, _embedder, null,
                toolsPerRound, null, ct).ConfigureAwait(false);

            foreach (var hit in hits)
                if (hit.Name != null) collected[hit.Name] = hit;

            if (round == maxRounds - 1) break;

            if (_l3Client != null)
            {
                var sufficiency = await JudgeToolSufficiency(userQuery, collected.Keys.ToList(), ct)
                    .ConfigureAwait(false);
                if (sufficiency.IsEnough) break;
                searchQuery = sufficiency.SuggestedRefinement ?? searchQuery + " 其他相关工具";
            }
        }

        _proactiveHistory.Add((userQuery, collected.Count > 0 ? 1 : 0, collected.Count));
        return collected.Values.ToList();
    }

    private async Task<(bool IsEnough, string? SuggestedRefinement)> JudgeToolSufficiency(
        string query, List<string> toolNames, CancellationToken ct)
    {
        if (_l3Client == null) return (true, null);

        var toolList = string.Join("\n", toolNames.Select(n => $"  - {n}"));
        var prompt = $@"
用户请求: {query}

已检索到以下工具:
{toolList}

请判断这些工具是否足够完成用户请求。
如果不够，请建议还需要什么类型的工具。
只输出 JSON: {{""enough"": true/false, ""suggestion"": ""如果不够给出建议""}}";

        var response = await _l3Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0f, MaxOutputTokens = 200 }, ct).ConfigureAwait(false);

        var text = response.Messages?.LastOrDefault()?.Text ?? "";
        try
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<SufficiencyResult>(text,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result != null) return (!result.Enough, result.Suggestion);
        }
        catch
        {
            // non-critical, best-effort
        }

        return (true, null);
    }

    private sealed record SufficiencyResult(bool Enough, string? Suggestion);

    /// <summary>
    /// Two-stage re-rank: ONNX MiniLM pseudo-cross-encoder (local, ~100ms) →
    /// L3 LLM fallback (remote, ~300ms).
    ///
    /// Pseudo-cross-encoder: embeds "query [SEP] tool_desc" concatenated text.
    /// The ONNX model processes query and tool tokens in the same attention window,
    /// producing token-level interactions that bi-encoders miss. Scores are
    /// cosine similarity against the query-only embedding.
    /// </summary>
    private async Task<List<ToolRegistry.ToolDef>> RerankAsync(
        string query, List<ToolRegistry.ToolDef> candidates, CancellationToken ct)
    {
        // Try ONNX pseudo-cross-encoder first
        try
        {
            return await OnnxCrossEncodeRerankAsync(query, candidates, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fall back to L3 LLM re-rank
            if (_l3Client != null)
            {
                try
                {
                    return await L3RerankAsync(query, candidates, ct).ConfigureAwait(false);
                }
                catch
                {
                    // non-critical, best-effort
                }
            }
            return candidates;
        }
    }

    /// <summary>
    /// ONNX MiniLM pseudo-cross-encoder: batch-embeds each "query [SEP] tool_desc"
    /// concatenated text, then ranks by cosine similarity against the query-only embedding.
    ///
    /// Bi-encoder limitation: query and tool are embedded separately → no token-level
    /// interaction. By concatenating them, the same ONNX model produces cross-attended
    /// representations — a poor man's cross-encoder at zero additional model cost.
    /// </summary>
    private async Task<List<ToolRegistry.ToolDef>> OnnxCrossEncodeRerankAsync(
        string query, List<ToolRegistry.ToolDef> candidates, CancellationToken ct)
    {
        var topN = candidates.Take(RerankCandidateN).ToList();

        // Query-only embedding — reuse from cache (ExpertRegistry already computed it)
        var queryEmb = _queryCache?.Get(query);
        if (queryEmb == null)
        {
            queryEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            _queryCache?.Set(query, queryEmb);
        }

        // Concatenated texts: "query [SEP] tool_name: description"
        var concatTexts = topN.Select(t =>
        {
            var desc = t.Description.Length > 200 ? t.Description[..200] : t.Description;
            return $"{query}\n{t.Name}: {desc}";
        }).ToArray();

        // Batch ONNX inference — one forward pass for all 20 candidates
        var concatEmbs = await _embedder.GenerateBatchAsync(concatTexts, ct).ConfigureAwait(false);

        // Score each by cosine similarity to query-only embedding
        var scored = new List<(int Index, float Score)>(topN.Count);
        for (int i = 0; i < topN.Count; i++)
        {
            var score = Cosine(queryEmb, concatEmbs[i]);
            scored.Add((i, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Reorder candidates by cross-encoder score
        var reranked = scored.Select(s => topN[s.Index]).ToList();
        // Append any remaining candidates that weren't in top-N
        if (candidates.Count > topN.Count)
            reranked.AddRange(candidates.Skip(topN.Count));

        return reranked;
    }

    /// <summary>
    /// L3 LLM cross-encoder re-rank: sends top-N candidates to the cheapest
    /// available LLM and asks it to pick the most relevant tools. Used only
    /// as fallback when ONNX embedding is unavailable.
    /// </summary>
    private async Task<List<ToolRegistry.ToolDef>> L3RerankAsync(
        string query, List<ToolRegistry.ToolDef> candidates, CancellationToken ct)
    {
        var topN = candidates.Take(RerankCandidateN).ToList();
        var lines = topN.Select((t, i) =>
        {
            var desc = t.Description.Length > 120 ? t.Description[..120] : t.Description;
            return $"  {i + 1}. {t.Name} ({t.Domain}): {desc}";
        });

        var prompt = $$"""
            Given this user query:
            
            {{query}}
            
            Select the {{DefaultTopK}} most relevant tools from this list. Return ONLY the tool names, one per line.
            
            {{string.Join('\n', lines)}}
            """;

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var response = await _l3Client!.GetResponseAsync(
            messages,
            new ChatOptions { Temperature = 0f, MaxOutputTokens = 200 },
            ct).ConfigureAwait(false);

        var text = response.Text ?? "";
        var selectedNames = ParseToolNames(text);

        if (selectedNames.Count == 0) return candidates;

        var nameSet = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var reranked = candidates
            .Where(t => nameSet.Contains(t.Name))
            .Concat(candidates.Where(t => !nameSet.Contains(t.Name)))
            .ToList();

        return reranked.Count > 0 ? reranked : candidates;
    }

    private static List<string> ParseToolNames(string l3Response)
    {
        var names = new List<string>();
        foreach (var line in l3Response.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimStart('-', '*', ' ', '\t', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
            if (trimmed.Length > 1 && !trimmed.Contains(' ') && !trimmed.Contains(':'))
                names.Add(trimmed);
        }
        return names;
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-9f ? 0f : dot / denom;
    }

    private static string GetLastUserQuery(IEnumerable<ChatMessage> messages)
    {
        var parts = new List<string>(2);
        foreach (var m in messages.Reverse())
        {
            if (m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            {
                parts.Add(m.Text.Trim());
                if (parts.Count >= 2) break;
            }
        }
        parts.Reverse();
        return string.Join(" ", parts);
    }
}
