using System.Text.Json;

namespace LTAI.Agent.Vector;

/// <summary>
/// #3 MemGraphRAG: multi-agent KG exploration traces.
/// When agents explore the knowledge graph, they leave structured traces
/// that other agents can discover and follow, enabling collaborative
/// graph construction and reducing redundant exploration.
/// </summary>
public sealed class KgExplorationTrace
{
    public sealed record ExplorationTrace(
        string TraceId,
        string AgentName,
        string Query,
        string[] EntitiesVisited,
        string[] RelationsFollowed,
        string[] Findings,
        double UtilityScore,   // how useful this trace was (0-1)
        DateTime Timestamp);

    private readonly string _storePath;
    private List<ExplorationTrace> _traces = [];

    public KgExplorationTrace(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), ".livingtree", "kg_traces.json");
        Load();
    }

    public IReadOnlyList<ExplorationTrace> Traces => _traces;

    /// <summary>Record an exploration trace from an agent's KG query.</summary>
    public void Record(string agentName, string query,
        string[] entities, string[] relations, string[] findings, double utility)
    {
        _traces.Add(new ExplorationTrace(
            TraceId: Guid.NewGuid().ToString("n"),
            AgentName: agentName,
            Query: query,
            EntitiesVisited: entities,
            RelationsFollowed: relations,
            Findings: findings,
            UtilityScore: utility,
            Timestamp: DateTime.UtcNow));

        // Keep only last 500 traces
        if (_traces.Count > 500)
            _traces = _traces[^500..];

        Save();
    }

    /// <summary>Find traces relevant to a query.</summary>
    public List<ExplorationTrace> FindRelevant(string query, int topK = 5)
    {
        var lower = query.ToLowerInvariant();
        return _traces
            .Select(t => (trace: t,
                score: (t.Query.Contains(lower, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
                     + t.EntitiesVisited.Count(e => lower.Contains(e.ToLowerInvariant())) * 2
                     + t.Findings.Count(f => lower.Contains(f.ToLowerInvariant()))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.trace.UtilityScore)
            .Take(topK)
            .Select(x => x.trace)
            .ToList();
    }

    /// <summary>Get traces by agent.</summary>
    public List<ExplorationTrace> ByAgent(string agentName) =>
        _traces.Where(t => t.AgentName.Equals(agentName, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(t => t.Timestamp).ToList();

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
                _traces = JsonSerializer.Deserialize<List<ExplorationTrace>>(File.ReadAllText(_storePath)) ?? [];
        }
        catch { _traces = []; }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_traces, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
