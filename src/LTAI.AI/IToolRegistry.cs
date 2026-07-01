using Microsoft.Extensions.AI;

namespace LTAI.AI;

/// <summary>Tool registry for dual-channel retrieval (BM25 + Vector + RRF).</summary>
public interface IToolRegistry
{
    /// <summary>True if the registry has been initialized at least once.</summary>
    bool IsInitialized { get; }

    /// <summary>Initialize the tool registry: compute embeddings and build BM25 index.</summary>
    Task InitializeAsync(IEnumerable<AITool> tools, EmbeddingClient embedder, ToolEmbeddingCache? cache = null, CancellationToken ct = default);

    /// <summary>Search top-K tools by query (full domain).</summary>
    Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, int k = 8, CancellationToken ct = default);

    /// <summary>Search top-K tools by query with domain weighting.</summary>
    Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k = 8, CancellationToken ct = default);

    /// <summary>Search top-K tools with precomputed query embedding.</summary>
    Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k, float[]? queryEmbedding, CancellationToken ct = default);

    /// <summary>Record a tool call result for metrics.</summary>
    void RecordCall(string toolName, bool success, long latencyMs);

    /// <summary>Get all tool invocation statistics.</summary>
    IReadOnlyDictionary<string, ToolRegistry.ToolStats> GetAllStats();

    /// <summary>Get stats for a specific tool.</summary>
    ToolRegistry.ToolStats? GetStats(string toolName);

    /// <summary>Reset all statistics.</summary>
    void ResetStats();

    /// <summary>Get all registered tools (snapshot, thread-safe).</summary>
    IReadOnlyList<ToolRegistry.ToolDef> AllTools { get; }

    /// <summary>Get tools by domain.</summary>
    IReadOnlyList<ToolRegistry.ToolDef> GetToolsByDomain(string domain);

    /// <summary>Get an AIFunction by tool name.</summary>
    AIFunction? GetToolByName(string name);

    /// <summary>Invoke a tool by name with argument dictionary and return result string.</summary>
    Task<string?> InvokeToolAsync(string name, Dictionary<string, object?> args, CancellationToken ct = default);

    /// <summary>Clear the registry (for testing or reload).</summary>
    void Clear();

    /// <summary>Mark all tool embeddings as stale for re-computation.</summary>
    void ClearEmbeddings();
}
