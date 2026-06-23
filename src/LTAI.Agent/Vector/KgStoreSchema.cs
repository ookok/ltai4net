namespace LTAI.Agent.Vector;

public static class KgStoreSchema
{
    /// <summary>Valid entity types for KgStore Nodes. Write-once known set.</summary>
    public static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "document", "concept", "fact", "note", "wiki", "chunk",
        "class", "method", "function", "interface", "enum", "struct", "record",
        "property", "field", "file", "module", "namespace",
        "person", "organization", "project", "tool", "location", "date",
        "incident", "decision", "event", "milestone",
    };

    /// <summary>Valid edge relation labels. Custom values accepted with warning.</summary>
    public static readonly HashSet<string> ValidRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "has_fact", "references", "calls", "implements",
        "extends", "depends_on", "related_to", "uses", "mentions",
        "causes", "fixes", "contradicts", "supports", "follows",
        "replaces", "tracked_in", "part_of", "created_by",
        "refines", "refutes",
        // Disco-RAG rhetorical relations
        "elaborates", "contrasts_with", "causes_effect",
        "supports_claim", "provides_background",
    };

    public static bool IsValidKind(string kind) => string.IsNullOrEmpty(kind) || ValidKinds.Contains(kind);
    public static bool IsValidRelation(string rel) => string.IsNullOrEmpty(rel) || ValidRelations.Contains(rel);

    public static string? ValidateNode(string kind, string? relation = null)
    {
        if (!string.IsNullOrEmpty(kind) && !ValidKinds.Contains(kind))
            return $"Invalid entity kind '{kind}'. Valid: {string.Join(", ", ValidKinds.Order())}";
        if (!string.IsNullOrEmpty(relation) && !ValidRelations.Contains(relation))
            return $"Invalid relation '{relation}'. Valid: {string.Join(", ", ValidRelations.Order())}";
        return null;
    }
}
