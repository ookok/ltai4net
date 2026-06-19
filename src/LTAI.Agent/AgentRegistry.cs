using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>Definition of Done criteria for this agent.</summary>
    public string? DoD { get; init; }
    /// <summary>WIP limit for this agent (0 = use global default).</summary>
    public int WipLimit { get; init; }

    /// <summary>Cached embedding vector for semantic routing.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Trigger keywords that activate this agent. Injected only when user query matches.</summary>
    public string[] Trigger { get; init; } = [];

    /// <summary>Estimated token cost when this agent's prompt is injected (0 = unknown).</summary>
    public int TokenEstimate { get; init; }

    /// <summary>Text used for embedding — combines description + tools.</summary>
    public string CapabilityText =>
        $"{Description} | tools: {string.Join(", ", Tools)} | {Prompt.Truncate(500)}";
}

/// <summary>
/// Registry for agent definitions loaded from <c>agents/*.agent.md</c>.
/// Provides both static convenience access (via <c>AgentRegistry.LoadAll()</c>)
/// and DI injectable <see cref="IAgentRegistry"/> interface.
/// The static methods delegate to a shared default instance.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private static readonly Lazy<AgentRegistry> _default = new(() => new AgentRegistry());

    private List<AgentFileDef>? _cached;
    private readonly object _cacheLock = new();

    // ═══════════════════════════════════════════
    //  Static convenience shims (backward compat)
    //  Delegate through _default.Value to Internal* methods
    // ═══════════════════════════════════════════

    public static List<AgentFileDef> LoadAll() => _default.Value.InternalLoadAll();
    public static void InvalidateCache() => _default.Value.InternalInvalidateCache();
    public static void ClearEmbeddings() => _default.Value.InternalClearEmbeddings();
    public static Task EnsureEmbeddingsAsync(EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, CancellationToken ct = default)
        => _default.Value.InternalEnsureEmbeddingsAsync(embedder, cache, ct);
    public static Task<string[]> SelectTopKAsync(string task, EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, int k = 5, CancellationToken ct = default)
        => _default.Value.InternalSelectTopKAsync(task, embedder, cache, k, ct);
    public static Task<IReadOnlyList<(string Name, float Score)>> SelectTopKWithScoresAsync(
        string task, EmbeddingClient embedder, ToolEmbeddingCache? cache = null,
        int k = 5, CancellationToken ct = default)
        => _default.Value.InternalSelectTopKWithScoresAsync(task, embedder, cache, k, ct);
    public static AgentFileDef? ParseFile(string path) => _default.Value.InternalParseFile(path);
    public static AgentFileDef? Parse(string text) => _default.Value.InternalParse(text);
    public static FileSystemWatcher? StartWatcher() => _default.Value.InternalStartWatcher();
    public static void StopWatcher() => _default.Value.InternalStopWatcher();

    // ═══════════════════════════════════════════
    //  IAgentRegistry explicit interface implementation
    //  (delegates to static methods via _default.Value)
    // ═══════════════════════════════════════════

    private List<AgentFileDef> InternalLoadAll()
    {
        if (_cached != null) return _cached;
        lock (_cacheLock)
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
                        var def = InternalParseFile(file);
                        if (def != null && !string.IsNullOrEmpty(def.Name))
                            result.Add(def);
                    }
                    catch (Exception) { /* skip malformed agent files — log at debug level */ }
                }
                break;
            }
            _cached = result;
            return result;
        }
    }

    private void InternalInvalidateCache() { lock (_cacheLock) _cached = null; }

    private void InternalClearEmbeddings()
    {
        lock (_cacheLock)
        {
            _cached = null;
            var agents = InternalLoadAll();
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i].Embedding != null)
                    agents[i] = agents[i] with { Embedding = null };
            }
        }
    }

    private async Task InternalEnsureEmbeddingsAsync(EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, CancellationToken ct = default)
    {
        var agents = InternalLoadAll();
        var pending = agents
            .Select((a, i) => (agent: a, index: i))
            .Where(x => x.agent.Embedding == null)
            .ToList();
        if (pending.Count == 0) return;

        if (cache != null)
        {
            var items = pending
                .Select(x => (x.agent.Name, x.agent.CapabilityText))
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
                await ComputeEmbeddingsAsync(agents, embedder, ct).ConfigureAwait(false);
            }
            return;
        }
        await ComputeEmbeddingsAsync(agents, embedder, ct).ConfigureAwait(false);
    }

    private async Task<string[]> InternalSelectTopKAsync(string task, EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null, int k = 5, CancellationToken ct = default)
    {
        var scored = await InternalSelectTopKWithScoresAsync(task, embedder, cache, k, ct).ConfigureAwait(false);
        return scored.Select(s => s.Name).ToArray();
    }

    private async Task<IReadOnlyList<(string Name, float Score)>> InternalSelectTopKWithScoresAsync(
        string task, EmbeddingClient embedder, ToolEmbeddingCache? cache = null,
        int k = 5, CancellationToken ct = default)
    {
        var agents = InternalLoadAll();
        if (agents.Count == 0) return [];

        var hasMissing = false;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].Embedding == null) { hasMissing = true; break; }
        }
        if (hasMissing)
        {
            await InternalEnsureEmbeddingsAsync(embedder, cache, ct).ConfigureAwait(false);
            agents = InternalLoadAll();
        }

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

        float[] taskEmb;
        try
        {
            taskEmb = await embedder.GenerateAsync(task, ct).ConfigureAwait(false);
        }
        catch
        {
            taskEmb = EmbeddingClient.FastEmb(task);
        }

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

    private AgentFileDef? InternalParseFile(string path)
    {
        var text = File.ReadAllText(path);
        return InternalParse(text);
    }

    private AgentFileDef? InternalParse(string text)
    {
        var match = Regex.Match(text, "^---[\r]?\n(.*?)[\r]?\n---[\r]?\n(.*)", RegexOptions.Singleline);
        if (!match.Success) return null;

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();
        var def = new AgentFileDef { Prompt = body };

        var lines = frontmatter.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var colonPos = trimmed.IndexOf(':');
            if (colonPos < 0) continue;

            var key = trimmed.Substring(0, colonPos).Trim().ToLowerInvariant();
            var rawVal = trimmed.Substring(colonPos + 1).Trim();

            if (string.IsNullOrEmpty(rawVal) && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("- "))
            {
                var listItems = new List<string>();
                i++;
                while (i < lines.Length)
                {
                    var listLine = lines[i].Trim();
                    if (listLine.StartsWith("- "))
                    {
                        listItems.Add(listLine[2..].Trim().Trim('"'));
                        i++;
                    }
                    else { i--; break; }
                }
                rawVal = "[" + string.Join(", ", listItems.Select(item => $"\"{item.Replace("\"", "\\\"")}\"")) + "]";
            }
            else if (!string.IsNullOrEmpty(rawVal) && !rawVal.StartsWith('['))
            {
                while (i + 1 < lines.Length)
                {
                    var nextLine = lines[i + 1];
                    if (!string.IsNullOrEmpty(nextLine) && nextLine[0] == ' ')
                    {
                        rawVal += " " + nextLine.Trim();
                        i++;
                    }
                    else break;
                }
            }

            var val = rawVal.Trim().Trim('"');

            switch (key)
            {
                case "name":          def = def with { Name = val }; break;
                case "description":   def = def with { Description = val }; break;
                case "temperature":   if (double.TryParse(val, out var t)) def = def with { Temperature = t }; break;
                case "topp":          if (double.TryParse(val, out var p)) def = def with { TopP = p }; break;
                case "modelid":       def = def with { ModelId = val }; break;
                case "inherittools":  def = def with { InheritTools = val.ToLowerInvariant() }; break;
                case "permissions":   def = def with { Permissions = ParseJsonArray(rawVal) ?? def.Permissions }; break;
                case "tools":         def = def with { Tools = ParseJsonArray(rawVal) ?? def.Tools }; break;
                case "dod":           def = def with { DoD = val }; break;
                case "wipLimit":      if (int.TryParse(val, out var w)) def = def with { WipLimit = w }; break;
                case "trigger":       def = def with { Trigger = ParseJsonArray(rawVal) ?? def.Trigger }; break;
                case "tokenestimate":
                case "token_estimate": if (int.TryParse(val, out var te)) def = def with { TokenEstimate = te }; break;
            }
        }
        return def;
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    private async Task ComputeEmbeddingsAsync(List<AgentFileDef> agents, EmbeddingClient embedder,
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
        => LTAI.AI.VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());

    private static string[]? ParseJsonArray(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed == "[]" || trimmed == "") return null;
        if (trimmed.StartsWith('[')) trimmed = trimmed[1..];
        if (trimmed.EndsWith(']')) trimmed = trimmed[..^1];
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var items = SplitCsvValues(trimmed)
            .Select(s => s.Trim().Trim('"').Trim('\''))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return items.Length > 0 ? items : null;
    }

    private static string[] SplitCsvValues(string input)
    {
        var results = new List<string>();
        var inQuotes = false;
        var start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '"') inQuotes = !inQuotes;
            else if (input[i] == ',' && !inQuotes)
            {
                results.Add(input[start..i]);
                start = i + 1;
            }
        }
        if (start < input.Length) results.Add(input[start..]);
        return results.ToArray();
    }

    private FileSystemWatcher? _agentWatcher;

    public FileSystemWatcher? InternalStartWatcher()
    {
        var dirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "agents"),
            Path.Combine(Directory.GetCurrentDirectory(), "agents"),
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            _agentWatcher = new FileSystemWatcher(dir, "*.agent.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _agentWatcher.Changed += (_, e) => InternalInvalidateCache();
            _agentWatcher.Created += (_, e) => InternalInvalidateCache();
            _agentWatcher.Deleted += (_, e) => InternalInvalidateCache();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => InternalStopWatcher();
            return _agentWatcher;
        }
        return null;
    }

    public void InternalStopWatcher()
    {
        try { _agentWatcher?.Dispose(); } catch
        {
            // non-critical, best-effort
        }
        _agentWatcher = null;
    }

    // ═══════════════════════════════════════════
    //  IAgentRegistry explicit interface implementation
    // ═══════════════════════════════════════════

    List<AgentFileDef> IAgentRegistry.LoadAll() => InternalLoadAll();
    void IAgentRegistry.InvalidateCache() => InternalInvalidateCache();
    void IAgentRegistry.ClearEmbeddings() => InternalClearEmbeddings();
    Task IAgentRegistry.EnsureEmbeddingsAsync(EmbeddingClient embedder, ToolEmbeddingCache? cache, CancellationToken ct)
        => InternalEnsureEmbeddingsAsync(embedder, cache, ct);
    Task<string[]> IAgentRegistry.SelectTopKAsync(string task, EmbeddingClient embedder, ToolEmbeddingCache? cache, int k, CancellationToken ct)
        => InternalSelectTopKAsync(task, embedder, cache, k, ct);
    Task<IReadOnlyList<(string Name, float Score)>> IAgentRegistry.SelectTopKWithScoresAsync(string task, EmbeddingClient embedder, ToolEmbeddingCache? cache, int k, CancellationToken ct)
        => InternalSelectTopKWithScoresAsync(task, embedder, cache, k, ct);
    AgentFileDef? IAgentRegistry.ParseFile(string path) => InternalParseFile(path);
    AgentFileDef? IAgentRegistry.Parse(string text) => InternalParse(text);
    FileSystemWatcher? IAgentRegistry.StartWatcher() => InternalStartWatcher();
    void IAgentRegistry.StopWatcher() => InternalStopWatcher();
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
