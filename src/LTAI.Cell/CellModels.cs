using System.Text.Json.Serialization;

namespace LTAI.Cell;

public sealed record TrainingTask
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = string.Empty;

    [JsonPropertyName("dataset_name")]
    public string DatasetName { get; init; } = string.Empty;

    [JsonPropertyName("hyper_params")]
    public Dictionary<string, string> HyperParams { get; init; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("epoch")]
    public int Epoch { get; set; }

    [JsonPropertyName("loss")]
    public float Loss { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record MitosisResult
{
    [JsonPropertyName("parent_id")]
    public string ParentId { get; init; } = string.Empty;

    [JsonPropertyName("child_id")]
    public string ChildId { get; init; } = string.Empty;

    [JsonPropertyName("forked_at")]
    public DateTime ForkedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("gene_count")]
    public int GeneCount { get; init; }

    [JsonPropertyName("traits")]
    public Dictionary<string, float> Traits { get; init; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record DistillationLog
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("teacher_model")]
    public string TeacherModel { get; init; } = string.Empty;

    [JsonPropertyName("student_model")]
    public string StudentModel { get; init; } = string.Empty;

    [JsonPropertyName("knowledge_keys")]
    public string[] KnowledgeKeys { get; init; } = Array.Empty<string>();

    [JsonPropertyName("compressed_count")]
    public int CompressedCount { get; init; }

    [JsonPropertyName("faithfulness_score")]
    public float FaithfulnessScore { get; init; }

    [JsonPropertyName("tokens_saved")]
    public int TokensSaved { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

public sealed record DreamCycle
{
    [JsonPropertyName("cycle")]
    public int Cycle { get; init; }

    [JsonPropertyName("patterns_discovered")]
    public string[] PatternsDiscovered { get; init; } = Array.Empty<string>();

    [JsonPropertyName("reflexes_improved")]
    public int ReflexesImproved { get; init; }

    [JsonPropertyName("dream_duration_ms")]
    public long DreamDurationMs { get; init; }

    [JsonPropertyName("insights_generated")]
    public int InsightsGenerated { get; init; }
}

public sealed record RegenReport
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; init; } = string.Empty;

    [JsonPropertyName("damage_score")]
    public float DamageScore { get; init; }

    [JsonPropertyName("healed")]
    public bool Healed { get; init; }

    [JsonPropertyName("cells_replaced")]
    public int CellsReplaced { get; init; }

    [JsonPropertyName("recovery_time_ms")]
    public long RecoveryTimeMs { get; init; }

    [JsonPropertyName("new_weights")]
    public Dictionary<string, float?> NewWeights { get; init; } = new();
}
