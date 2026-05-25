using System.Collections.Concurrent;

namespace LTAI.Knowledge.Core;

public sealed record SourceCitation
{
    public string SourceId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string ChunkId { get; init; } = "";
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public double Confidence { get; init; }
    public string EvidenceSnippet { get; init; } = "";
}

public sealed record ArtifactField
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Type { get; init; } = "string";
    public List<SourceCitation> Citations { get; init; } = new();
    public double Confidence { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed record KnowledgeArtifact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "general";
    public string Description { get; init; } = "";
    public List<ArtifactField> Fields { get; init; } = new();
    public int Version { get; init; } = 1;
    public DateTimeOffset CompiledAt { get; init; } = DateTimeOffset.UtcNow;
    public string CompiledFor { get; init; } = "";
    public double EvalScore { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new();
}

public sealed record ArtifactContext
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public List<string> ArtifactIds { get; init; } = new();
    public List<string> SourceDocumentIds { get; init; } = new();
    public Dictionary<string, string> AccessPolicy { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ArtifactStore
{
    private readonly ConcurrentDictionary<string, KnowledgeArtifact> _artifacts = new();
    private readonly ConcurrentDictionary<string, ArtifactContext> _contexts = new();
    private readonly ConcurrentDictionary<string, List<string>> _contextArtifacts = new();
    private readonly object _lock = new();

    public string StoreArtifact(KnowledgeArtifact artifact)
    {
        _artifacts[artifact.Id] = artifact;
        return artifact.Id;
    }

    public KnowledgeArtifact? GetArtifact(string id)
    {
        _artifacts.TryGetValue(id, out var a);
        return a;
    }

    public List<KnowledgeArtifact> GetArtifactsByDomain(string domain)
    {
        return _artifacts.Values
            .Where(a => a.Domain == domain)
            .OrderByDescending(a => a.EvalScore)
            .ToList();
    }

    public List<KnowledgeArtifact> GetArtifactsByContext(string contextId)
    {
        if (_contextArtifacts.TryGetValue(contextId, out var ids))
            return ids.Select(id => _artifacts.TryGetValue(id, out var a) ? a : null)
                .Where(a => a != null)
                .Cast<KnowledgeArtifact>()
                .ToList();

        return new();
    }

    public void CreateContext(ArtifactContext context)
    {
        _contexts[context.Id] = context;
        _contextArtifacts[context.Id] = context.ArtifactIds;
    }

    public ArtifactContext? GetContext(string id)
    {
        _contexts.TryGetValue(id, out var ctx);
        return ctx;
    }

    public List<ArtifactContext> GetAllContexts()
        => _contexts.Values.ToList();

    public bool CheckAccess(string contextId, string role)
    {
        if (!_contexts.TryGetValue(contextId, out var ctx))
            return false;

        if (ctx.AccessPolicy.TryGetValue("roles", out var roles))
        {
            var allowed = roles.Split(',', StringSplitOptions.TrimEntries);
            return allowed.Contains(role) || allowed.Contains("*");
        }
        return true;
    }

    public List<KnowledgeArtifact> QueryByTags(List<string> tags, string? domain = null)
    {
        return _artifacts.Values
            .Where(a => (domain == null || a.Domain == domain)
                && tags.Any(t => a.Tags.ContainsKey(t)))
            .OrderByDescending(a => a.EvalScore)
            .ToList();
    }

    public KnowledgeArtifact? UpdateArtifact(KnowledgeArtifact updated)
    {
        lock (_lock)
        {
            if (_artifacts.TryGetValue(updated.Id, out var existing))
            {
                var versioned = updated with { Version = existing.Version + 1 };
                _artifacts[versioned.Id] = versioned;
                return versioned;
            }
            return null;
        }
    }

    public int RemoveArtifact(string id)
    {
        if (_artifacts.TryRemove(id, out _)) return 1;
        return 0;
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["total_artifacts"] = _artifacts.Count,
            ["total_contexts"] = _contexts.Count,
            ["domains"] = _artifacts.Values.GroupBy(a => a.Domain)
                .ToDictionary(g => g.Key, g => g.Count()),
            ["avg_eval_score"] = Math.Round(
                _artifacts.Values.Average(a => a.EvalScore), 3),
            ["avg_fields"] = Math.Round(
                _artifacts.Values.Average(a => a.Fields.Count), 1),
            ["total_citations"] = _artifacts.Values
                .Sum(a => a.Fields.Sum(f => f.Citations.Count))
        };
    }

    public async Task SaveToDiskAsync(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "artifacts.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        var data = System.Text.Json.JsonSerializer.Serialize(new { artifacts = _artifacts, saved_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        await File.WriteAllTextAsync(path, data).ConfigureAwait(false);
    }

    public void LoadFromDisk(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "artifacts.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("artifacts", out var artifacts))
                foreach (var a in artifacts.EnumerateArray())
                {
                    var artifact = System.Text.Json.JsonSerializer.Deserialize<KnowledgeArtifact>(a.GetRawText());
                    if (artifact != null) _artifacts[artifact.Id] = artifact;
                }
        }
        catch { }
    }
}

public sealed class ProvenanceTracker
{
    private readonly ConcurrentDictionary<string, List<SourceCitation>> _fieldProvenance = new();
    private readonly ConcurrentDictionary<string, List<(string version, double score)>> _versionHistory = new();

    public void TrackField(string fieldKey, SourceCitation citation)
    {
        _fieldProvenance.AddOrUpdate(fieldKey,
            _ => new List<SourceCitation> { citation },
            (_, list) => { lock (list) list.Add(citation); return list; });
    }

    public void TrackVersion(string artifactId, int version, double evalScore)
    {
        _versionHistory.AddOrUpdate(artifactId,
            _ => new() { ($"v{version}", evalScore) },
            (_, list) => { lock (list) list.Add(($"v{version}", evalScore)); return list; });
    }

    public List<SourceCitation> GetFieldProvenance(string fieldKey)
    {
        _fieldProvenance.TryGetValue(fieldKey, out var list);
        return list ?? new();
    }

    public List<(string version, double score)> GetVersionHistory(string artifactId)
    {
        _versionHistory.TryGetValue(artifactId, out var list);
        return list ?? new();
    }

    public string BuildProvenanceChain(string artifactId, string fieldName)
    {
        var fieldKey = $"{artifactId}::{fieldName}";
        var citations = GetFieldProvenance(fieldKey);
        return string.Join(" | ",
            citations.Select(c =>
                $"[{c.SourcePath}@{c.StartOffset}-{c.EndOffset} conf={c.Confidence:F2}]"));
    }

    public Dictionary<string, int> GetSourceUsageStats()
    {
        return _fieldProvenance.Values
            .SelectMany(v => v)
            .GroupBy(c => c.SourceId)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
