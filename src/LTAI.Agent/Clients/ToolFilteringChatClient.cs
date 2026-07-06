using System.Runtime.CompilerServices;
using LTAI.AI;
using LTAI.Agent.Experts;
using LTAI.Agent.Memory;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;
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
    private readonly MetaSkillStore? _metaSkillStore;
    private string? _lastQuery;
    private int _lastToolCount;
    private readonly MemoryCache _resultCache = new(new MemoryCacheOptions());
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>多轮主动检索统计: (query, rounds, toolsFound) 记录。</summary>
    public IReadOnlyList<(string Query, int Rounds, int ToolsFound)> ProactiveRetrievalHistory =>
        _proactiveHistory.AsReadOnly();
    private readonly List<(string, int, int)> _proactiveHistory = [];

    /// <summary>Last Verbal-R3 tool annotations for observability.</summary>
    public IReadOnlyList<VerbalAnnotation>? LastToolAnnotations => _lastToolAnnotations;
    private List<VerbalAnnotation>? _lastToolAnnotations;

    public ToolFilteringChatClient(IChatClient inner, EmbeddingClient embedder,
        IToolRegistry toolRegistry,
        ToolEmbeddingCache? cache = null, QueryEmbeddingCache? queryCache = null,
        IChatClient? l3Client = null, MetaSkillStore? metaSkillStore = null)
    {
        _inner = inner;
        _embedder = embedder;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _queryCache = queryCache;
        _l3Client = l3Client;
        _metaSkillStore = metaSkillStore;
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
            // Cache key includes tool names hash (not just count) to detect tool set changes
            var toolNamesHash = string.Join(",", tools.Select(t => t.Name ?? "").OrderBy(n => n)).GetHashCode(StringComparison.Ordinal);
            var cacheKey = $"tf:{query}|{toolNamesHash}";
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

            // ── SkillWeaver fast path: use CompositionPlan instead of full retrieval ──
            var plan = CompositionPlanContext.Current;
            if (plan != null && plan.SubTasks.Count > 0)
            {
                return FilterByPlan(messages, plan, options);
            }

            // ── Meta-Skill domain hints: boost tool domains suggested by evolved WorkflowOrchestration ──
            var skillDomains = GetMetaSkillToolDomains();
            var queryEmb = _queryCache?.Get(query);
            // Stage 1: BM25 + ONNX bi-encoder → RRF fusion → top-20 candidates
            var hits = await _toolRegistry.SearchTopKAsync(query, _embedder, skillDomains,
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
        var setKey = $"tf:{query}|{string.Join(",", selectedTools.Select(t => t.Name ?? "").OrderBy(n => n)).GetHashCode(StringComparison.Ordinal)}";
        _resultCache.Set(setKey, clone, ResultCacheTtl);
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
        var actualRounds = 0;

        for (int round = 0; round < maxRounds; round++)
        {
            actualRounds = round + 1;
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

        _proactiveHistory.Add((userQuery, actualRounds, collected.Count));
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
            // LLM returned non-JSON — assume insufficient (continue searching).
            // Previous behavior was (true, null) which prematurely terminated.
        }

        // Default: not enough — continue the retrieval loop.
        // Previous behavior was (true, null) which prematurely terminated.
        return (false, null);
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
    /// available LLM and asks it to select the most relevant tools with
    /// Verbal-R3 verbal annotations explaining each selection.
    /// Used as fallback when ONNX embedding is unavailable.
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
            
            Select the {{DefaultTopK}} most relevant tools from this list. For each selected tool, explain WHY it is relevant.
            
            {{string.Join('\n', lines)}}
            
            Return a JSON array of {"name": "ToolName", "rationale": "why this tool is relevant", "confidence": "high|medium|low"}.
            """;

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var response = await _l3Client!.GetResponseAsync(
            messages,
            new ChatOptions { Temperature = 0f, MaxOutputTokens = 500 },
            ct).ConfigureAwait(false);

        var text = response.Text ?? "";

        // Try to parse JSON annotations
        var annotations = ParseToolAnnotations(text);
        if (annotations.Count > 0)
        {
            _lastToolAnnotations = annotations;
            var selectedNames = new HashSet<string>(
                annotations.Select(a => a.SourceId).Where(n => n != null)!,
                StringComparer.OrdinalIgnoreCase);

            if (selectedNames.Count > 0)
            {
                // Inject annotation rationale into tool descriptions for the Generator
                var annotatedCandidates = candidates
                    .Select(t =>
                    {
                        var ann = annotations.FirstOrDefault(a =>
                            string.Equals(a.SourceId, t.Name, StringComparison.OrdinalIgnoreCase));
                        if (ann != null && !string.IsNullOrWhiteSpace(ann.Rationale))
                        {
                            var desc = t.Description;
                            if (!desc.Contains("分析:"))
                                desc = $"{desc} (分析:{ann.Rationale})";
                            return new ToolRegistry.ToolDef(t.Name, desc, t.Embedding, t.Domain);
                        }
                        return t;
                    })
                    .ToList();

                return annotatedCandidates
                    .Where(t => selectedNames.Contains(t.Name))
                    .Concat(annotatedCandidates.Where(t => !selectedNames.Contains(t.Name)))
                    .ToList();
            }
        }

        // Fallback: parse plain tool names
        var selectedNamesFallback = ParseToolNames(text);
        if (selectedNamesFallback.Count == 0) return candidates;

        var nameSet = new HashSet<string>(selectedNamesFallback, StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(t => nameSet.Contains(t.Name))
            .Concat(candidates.Where(t => !nameSet.Contains(t.Name)))
            .ToList();
    }

    /// <summary>
    /// Parse Verbal-R3 tool annotations from JSON array response.
    /// </summary>
    private static List<VerbalAnnotation> ParseToolAnnotations(string text)
    {
        try
        {
            var startIdx = text.IndexOf('[');
            var endIdx = text.LastIndexOf(']');
            if (startIdx < 0 || endIdx <= startIdx) return [];

            text = text[startIdx..(endIdx + 1)];
            var items = System.Text.Json.JsonSerializer.Deserialize<List<ToolAnnotationItem>>(text,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (items == null || items.Count == 0) return [];

            return items.Select(i => new VerbalAnnotation
            {
                Score = i.Confidence?.ToLowerInvariant() switch
                {
                    "high" => 0.9f,
                    "medium" => 0.5f,
                    _ => 0.2f
                },
                Rationale = i.Rationale ?? "",
                Confidence = i.Confidence?.ToLowerInvariant() switch
                {
                    "high" => AnnotationConfidence.High,
                    "medium" => AnnotationConfidence.Medium,
                    _ => AnnotationConfidence.Low
                },
                SourceId = i.Name
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed record ToolAnnotationItem(string Name, string? Rationale, string? Confidence);

    private static List<string> ParseToolNames(string l3Response)
    {
        var names = new List<string>();
        foreach (var line in l3Response.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Trim punctuation/whitespace prefix but NOT digits (e.g. "2FAValidator" is valid)
            var trimmed = line.Trim().TrimStart('-', '*', ' ', '\t', '.');
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

    /// <summary>
    /// SkillWeaver: filter tools using CompositionPlan instead of full BM25+Vector+RRF retrieval.
    /// Scans messages for completed tool results, determines the current DAG group,
    /// and returns only the tools assigned to that group + pinned tools.
    /// This reduces the LLM-visible tool set from 8+ candidates to 1-3 relevant tools,
    /// achieving the 99% token reduction that SkillWeaver (arXiv 2606.18051) describes.
    /// </summary>
    private static ChatOptions? FilterByPlan(
        IEnumerable<ChatMessage> messages,
        CompositionPlan plan,
        ChatOptions? options)
    {
        if (options?.Tools is null || options.Tools.Count == 0)
            return options;

        var completedTools = GetCompletedToolNames(messages);
        var currentGroup = FindCurrentDagGroup(plan, completedTools);

        // All groups complete or past the end → show all tools
        if (currentGroup < 0 || currentGroup >= plan.ExecutionGroups.Count)
            return options;

        var group = plan.ExecutionGroups[currentGroup];
        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var taskIdx in group)
        {
            var tool = plan.SubTasks[taskIdx].AssignedTool;
            if (tool != null)
                allowedNames.Add(tool);
        }

        // Always include pinned tools
        foreach (var t in PinnedTools)
            allowedNames.Add(t);

        var tools = options.Tools.ToList();
        var selected = new List<AITool>(allowedNames.Count);

        foreach (var t in tools)
        {
            var name = t.Name ?? "";
            if (allowedNames.Contains(name))
                selected.Add(t);
        }

        // ── Token savings tracking ──
        // Standard retrieval shows 8 tools × ~100 tok each = 800 tok.
        // Plan shows N tools for the current DAG group.
        var naiveTokens = DefaultTopK * 100;
        var actualTokens = Math.Max(1, selected.Count * 100);
        TokenSavingsTracker.RecordLookup(naiveTokens, actualTokens);

        // Fallback: if selection is empty (e.g. tools not registered), return all
        if (selected.Count == 0)
            return options;

        var clone = options.Clone();
        clone.Tools = selected;
        return clone;
    }

    /// <summary>
    /// Scan messages for completed tool results by matching
    /// FunctionCallContent.CallId with FunctionResultContent.CallId.
    /// </summary>
    private static HashSet<string> GetCompletedToolNames(IEnumerable<ChatMessage> messages)
    {
        var calls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc &&
                    fc.CallId != null && fc.Name != null)
                    calls[fc.CallId] = fc.Name;
            }
        }

        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc && frc.CallId != null)
                {
                    if (calls.TryGetValue(frc.CallId, out var name))
                        completed.Add(name);
                }
            }
        }

        return completed;
    }

    /// <summary>
    /// Find the first DAG group that has incomplete tools.
    /// Returns group index, or plan.ExecutionGroups.Count if all complete.
    /// </summary>
    private static int FindCurrentDagGroup(CompositionPlan plan, HashSet<string> completedTools)
    {
        for (int g = 0; g < plan.ExecutionGroups.Count; g++)
        {
            var allComplete = plan.ExecutionGroups[g].All(idx =>
            {
                var tool = plan.SubTasks[idx].AssignedTool;
                return tool == null || completedTools.Contains(tool);
            });

            if (!allComplete)
                return g;
        }

        return plan.ExecutionGroups.Count; // all complete
    }

    /// <summary>
    /// Extract tool domain hints from the current Meta-Skill's orchestration principles.
    /// The Meta-Skill evolves over rounds and may suggest domain-specific strategies
    /// that inform tool selection even without a full CompositionPlan.
    /// </summary>
    private string? GetMetaSkillToolDomains()
    {
        if (_metaSkillStore == null) return null;

        try
        {
            var skill = _metaSkillStore.Current;
            var allPrinciples = new List<string>();
            allPrinciples.AddRange(skill.TaskDecomposition.Principles);
            allPrinciples.AddRange(skill.AgentEngineering.Principles);
            allPrinciples.AddRange(skill.WorkflowOrchestration.Principles);

            var combined = string.Join(" ", allPrinciples).ToLowerInvariant();

            // Keyword → domain mapping. Multiple matches concatenated.
            var domains = new List<string>();
            if (combined.Contains("code") || combined.Contains("symbol") || combined.Contains("graph"))
                domains.Add("code");
            if (combined.Contains("research") || combined.Contains("search") || combined.Contains("web"))
                domains.Add("web");
            if (combined.Contains("data") || combined.Contains("database") || combined.Contains("query"))
                domains.Add("data");
            if (combined.Contains("file") || combined.Contains("read") || combined.Contains("write"))
                domains.Add("file");
            if (combined.Contains("git") || combined.Contains("version") || combined.Contains("commit"))
                domains.Add("git");

            return domains.Count > 0 ? string.Join(",", domains) : null;
        }
        catch
        {
            return null;
        }
    }
}
