using LTAI.AI;

namespace LTAI.Agent;

/// <summary>Registry for agent definitions loaded from <c>agents/*.agent.md</c>.</summary>
public interface IAgentRegistry
{
    /// <summary>Load all agent definitions (cached after first call).</summary>
    List<AgentFileDef> LoadAll();

    /// <summary>Invalidate the cached agent definition list.</summary>
    void InvalidateCache();

    /// <summary>Reset all cached embeddings so they are recomputed on next access.</summary>
    void ClearEmbeddings();

    /// <summary>Ensure embeddings are computed for all agent definitions.</summary>
    Task EnsureEmbeddingsAsync(EmbeddingClient embedder, ToolEmbeddingCache? cache = null, CancellationToken ct = default);

    /// <summary>Select top-K agent names by semantic similarity to the task.</summary>
    Task<string[]> SelectTopKAsync(string task, EmbeddingClient embedder, ToolEmbeddingCache? cache = null, int k = 5, CancellationToken ct = default);

    /// <summary>Top-K agents with cosine similarity scores for decision-tree routing.</summary>
    Task<IReadOnlyList<(string Name, float Score)>> SelectTopKWithScoresAsync(string task, EmbeddingClient embedder, ToolEmbeddingCache? cache = null, int k = 5, CancellationToken ct = default);

    /// <summary>Parse a single agent definition file.</summary>
    AgentFileDef? ParseFile(string path);

    /// <summary>Parse agent YAML front-matter text.</summary>
    AgentFileDef? Parse(string text);

    /// <summary>Start FileSystemWatcher for hot-reload of agent definitions.</summary>
    FileSystemWatcher? StartWatcher();

    /// <summary>Stop the FileSystemWatcher.</summary>
    void StopWatcher();
}
