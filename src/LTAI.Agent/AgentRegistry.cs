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
    /// Compute embeddings for all agents (lazy, cached per def).
    /// Call this once at startup or when agents change.
    /// </summary>
    public static async Task EnsureEmbeddingsAsync(EmbeddingClient embedder,
        CancellationToken ct = default)
    {
        var agents = LoadAll();
        foreach (var def in agents)
        {
            if (def.Embedding != null) continue; // already computed
            try
            {
                var emb = await embedder.GenerateAsync(def.CapabilityText, ct).ConfigureAwait(false);
                // Since AgentFileDef is a record, we need to update via index
                var idx = agents.IndexOf(def);
                if (idx >= 0)
                    agents[idx] = def with { Embedding = emb };
            }
            catch
            {
                // Skip agent if embedding fails — will not be selectable via vector
            }
        }
    }

    /// <summary>
    /// Select top-K agents by semantic similarity to the task.
    /// Uses ONNX embeddings (priority 1) or BM25 fallback.
    /// Returns agent names suitable for routing.
    /// Falls back to all agent names if embeddings not computed yet.
    /// </summary>
    public static async Task<string[]> SelectTopKAsync(string task, EmbeddingClient embedder,
        int k = 5, CancellationToken ct = default)
    {
        var agents = LoadAll();
        if (agents.Count == 0) return [];

        // Ensure embeddings (compute on demand if missing)
        var hasMissing = agents.Any(a => a.Embedding == null);
        if (hasMissing)
        {
            await ComputeEmbeddingsAsync(agents, embedder, ct).ConfigureAwait(false);
        }

        // If still no embeddings (embedder unavailable), return all
        if (agents.All(a => a.Embedding == null))
            return agents.Select(a => a.Name).Where(n => n != null).Cast<string>().Take(k).ToArray();

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

        var scored = agents
            .Where(a => a.Embedding != null)
            .Select(a => (name: a.Name, score: CosineSimilarity(taskEmb, a.Embedding!)))
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.name)
            .Where(n => n != null)
            .Cast<string>()
            .ToArray();

        return scored.Length > 0 ? scored : agents.Select(a => a.Name).Cast<string>().Take(k).ToArray();
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

    private static string[]? ParseJsonArray(string json)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(json);
            return arr?.Length > 0 ? arr : null;
        }
        catch { return null; }
    }
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
