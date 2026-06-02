using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;

namespace LTAI.Agent;

public sealed record AgentFileDef
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.95;
    public string? ModelId { get; init; }
    public string InheritTools { get; init; } = "";
    /// <summary>Permission flags: "read", "write", "list", "exec".</summary>
    public string[] Permissions { get; init; } = [];
    /// <summary>Tool category names enabled for this agent.</summary>
    public string[] Tools { get; init; } = [];
    public string Prompt { get; init; } = "";

    /// <summary>Cached embedding vector for semantic routing.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Text used for embedding — combines description + tools.</summary>
    public string CapabilityText =>
        $"{Description} | tools: {string.Join(", ", Tools)} | {Prompt.Truncate(200)}";
}

public static class AgentRegistry
{
    private static List<AgentFileDef>? _cached;

    public static List<AgentFileDef> LoadAll()
    {
        if (_cached != null) return _cached;

        var result = new List<AgentFileDef>();
        var searchDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "agents"),
            Path.Combine(Directory.GetCurrentDirectory(), "agents"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agents"),  // repo root
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.agent.md"))
            {
                try
                {
                    var def = ParseFile(file);
                    if (def != null && !string.IsNullOrEmpty(def.Name))
                        result.Add(def);
                }
                catch { }
            }
            break;
        }
        _cached = result;
        return result;
    }

    /// <summary>Invalidate cache (e.g., when agents/*.agent.md files change).</summary>
    public static void InvalidateCache() => _cached = null;

    /// <summary>
    /// P14.8: reset <see cref="AgentFileDef.Embedding"/> to <c>null</c> for
    /// every loaded agent so the next <see cref="EnsureEmbeddingsAsync"/>
    /// call re-embeds with the active model. Does not touch the def list
    /// itself — only the cached vectors.
    /// </summary>
    public static void ClearEmbeddings()
    {
        if (_cached == null) return;
        for (int i = 0; i < _cached.Count; i++)
        {
            if (_cached[i].Embedding != null)
                _cached[i] = _cached[i] with { Embedding = null };
        }
    }

    /// <summary>
    /// Compute embeddings for all agents (lazy, cached per def).
    /// Call this once at startup or when agents change.
    /// </summary>
    /// <remarks>
    /// P12.1: if a <see cref="ToolEmbeddingCache"/> is supplied, the 10 agent
    /// descriptions are sent in a single batched ONNX call and persisted to
    /// <c>%LOCALAPPDATA%/LTAI/tool_embeddings.json</c> keyed by SHA-256 of
    /// the capability text. On second call (or process restart) the cache
    /// hit eliminates the embedding work entirely.
    /// </remarks>
    public static async Task EnsureEmbeddingsAsync(EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, CancellationToken ct = default)
    {
        var agents = LoadAll();
        if (cache != null)
        {
            // P12.1: 1 batched call, persisted to disk
            var items = agents
                .Select(a => (a.Name, a.CapabilityText))
                .ToList();
            try
            {
                var vectors = await cache.GetOrComputeAllAsync(items, ct).ConfigureAwait(false);
                for (int i = 0; i < agents.Count; i++)
                {
                    if (vectors.TryGetValue(agents[i].Name, out var v) && v != null)
                        agents[i] = agents[i] with { Embedding = v };
                }
            }
            catch
            {
                // Cache path failed — fall through to per-agent computation
                await ComputeEmbeddingsAsync(agents, embedder, ct).ConfigureAwait(false);
            }
            return;
        }
        // Original sequential per-agent fallback (no cache)
        await ComputeEmbeddingsAsync(agents, embedder, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Select top-K agents by semantic similarity to the task.
    /// Uses ONNX embeddings (priority 1) or BM25 fallback.
    /// Returns agent names suitable for routing.
    /// Falls back to all agent names if embeddings not computed yet.
    /// </summary>
    public static async Task<string[]> SelectTopKAsync(string task, EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, int k = 5, CancellationToken ct = default)
    {
        var scored = await SelectTopKWithScoresAsync(task, embedder, cache, k, ct).ConfigureAwait(false);
        return scored.Select(s => s.Name).ToArray();
    }

    /// <summary>
    /// Top-K agents with cosine similarity scores. Used by the decision-tree
    /// router (P7.7) to compute the confidence margin between rank-1 and rank-2:
    /// a large margin → trust the top-K; a small margin → ambiguous, fall back
    /// to all specialists.
    /// </summary>
    /// <remarks>
    /// P12.1: pass a <see cref="ToolEmbeddingCache"/> to skip the initial
    /// batched embedding of agent descriptions on subsequent calls.
    /// </remarks>
    public static async Task<IReadOnlyList<(string Name, float Score)>> SelectTopKWithScoresAsync(
        string task, EmbeddingClient embedder, ToolEmbeddingCache? cache = null,
        int k = 5, CancellationToken ct = default)
    {
        var agents = LoadAll();
        if (agents.Count == 0) return [];

        // Ensure embeddings — single pass, no duplicate All() calls
        var hasMissing = false;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].Embedding == null) { hasMissing = true; break; }
        }
        if (hasMissing)
        {
            await EnsureEmbeddingsAsync(embedder, cache, ct).ConfigureAwait(false);
            // Re-read in case EnsureEmbeddingsAsync replaced the list
            agents = LoadAll();
        }

        // Check if embedder is unavailable after ensuring (all null → BM25 fallback)
        var anyEmbedding = false;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].Embedding != null) { anyEmbedding = true; break; }
        }
        if (!anyEmbedding)
        {
            var fallback = new List<(string, float)>(Math.Min(k, agents.Count));
            for (int i = 0; i < agents.Count && i < k; i++)
            {
                if (agents[i].Name != null)
                    fallback.Add((agents[i].Name!, 0f));
            }
            return fallback;
        }

        // Compute task embedding using ONNX (priority 1) → FastEmb fallback
        float[] taskEmb;
        try
        {
            taskEmb = await embedder.GenerateAsync(task, ct).ConfigureAwait(false);
        }
        catch
        {
            taskEmb = EmbeddingClient.FastEmb(task);
        }

        // Pre-allocate scored list to avoid LINQ enumerator allocations
        var candidates = new List<(string name, float score)>(agents.Count);
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Embedding != null && a.Name != null)
                candidates.Add((a.Name!, CosineSimilarity(taskEmb, a.Embedding!)));
        }
        candidates.Sort((a, b) => b.score.CompareTo(a.score));
        if (candidates.Count > k) candidates.RemoveRange(k, candidates.Count - k);
        return candidates;
    }

    // ═══════════════════════════════════════════
    //  Parser
    // ═══════════════════════════════════════════

    public static AgentFileDef? ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(text);
    }

    public static AgentFileDef? Parse(string text)
    {
        var match = Regex.Match(text, "^---[\r]?\n(.*?)[\r]?\n---[\r]?\n(.*)", RegexOptions.Singleline);
        if (!match.Success) return null;

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();
        var def = new AgentFileDef { Prompt = body };

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var colonPos = trimmed.IndexOf(':');
            if (colonPos < 0) continue;

            var key = trimmed.Substring(0, colonPos).Trim().ToLowerInvariant();
            var val = trimmed.Substring(colonPos + 1).Trim().Trim('"');

            switch (key)
            {
                case "name":          def = def with { Name = val }; break;
                case "description":   def = def with { Description = val }; break;
                case "temperature":   if (double.TryParse(val, out var t)) def = def with { Temperature = t }; break;
                case "topp":          if (double.TryParse(val, out var p)) def = def with { TopP = p }; break;
                case "modelid":       def = def with { ModelId = val }; break;
                case "inherittools":  def = def with { InheritTools = val.ToLowerInvariant() }; break;
                case "permissions":   def = def with { Permissions = ParseJsonArray(val) ?? def.Permissions }; break;
                case "tools":         def = def with { Tools = ParseJsonArray(val) ?? def.Tools }; break;
            }
        }
        return def;
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    private static async Task ComputeEmbeddingsAsync(List<AgentFileDef> agents, EmbeddingClient embedder,
        CancellationToken ct = default)
    {
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].Embedding != null) continue;
            try
            {
                var emb = await embedder.GenerateAsync(agents[i].CapabilityText, ct)
                    .ConfigureAwait(false);
                agents[i] = agents[i] with { Embedding = emb };
            }
            catch
            {
                // Skip individual failures
            }
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    private static string[]? ParseJsonArray(string raw)
    {
        // Strip surrounding brackets and whitespace; tolerate both quoted JSON arrays
        // (`["a", "b"]`) and unquoted CSV-style values (`[a, b]`) which some hand-edited
        // agent.md files use. Returns null for empty input.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[')) trimmed = trimmed[1..];
        if (trimmed.EndsWith(']')) trimmed = trimmed[..^1];
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var items = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim().Trim('"').Trim('\''))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return items.Length > 0 ? items : null;
    }
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
