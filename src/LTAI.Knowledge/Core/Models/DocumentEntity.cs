using System.Text.Json.Serialization;

namespace LTAI.Knowledge.Core.Models;

public sealed class DocumentEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Domain { get; set; } = "general";
    public string Category { get; set; } = "document";
    public string Source { get; set; } = "manual";
    public string Author { get; set; } = "system";
    public int Revision { get; set; } = 1;
    public double Importance { get; set; }
    public string? ParentId { get; set; }
    public string SectionPath { get; set; } = string.Empty;
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public double CreatedAt { get; set; }
    public double UpdatedAt { get; set; }
    public string Metadata { get; set; } = "{}";
    public string? DocId { get; set; }
    public int? ChunkIndex { get; set; }
    public int? StartChar { get; set; }
}

public sealed class RelationEntity
{
    public int Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Relation { get; set; } = "references";
    public double Weight { get; set; } = 1.0;
    public string Properties { get; set; } = "{}";
    public double CreatedAt { get; set; }
}

public sealed record KnowledgeSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; init; }
}

public sealed class DocumentStoreStats
{
    public int TotalDocuments { get; set; }
    public int TotalChunks { get; set; }
    public int TotalRelations { get; set; }
    public int TotalVectors { get; set; }
    public long DatabaseSizeBytes { get; set; }
}

public sealed class ChunkInfo
{
    public string Text { get; set; } = string.Empty;
    public int StartChar { get; set; }
}
