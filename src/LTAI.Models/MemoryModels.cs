namespace LTAI.Models;

public enum MemoryMode
{
    Classic,
    Files
}

public sealed record MemoryFileTrigger
{
    public string Pattern { get; init; } = "";
    public float Weight { get; init; } = 1.0f;
}

public sealed record MemoryFileFact
{
    public string Statement { get; init; } = "";
    public double Confidence { get; init; } = 1.0;
    public string? Source { get; init; }
}

public sealed record MemoryFileVerification
{
    public DateTime? LastVerified { get; init; }
    public string VerifiedBy { get; init; } = "none";
    public bool IsStale => LastVerified == null || (DateTime.UtcNow - LastVerified.Value).TotalDays > 30;
}

public sealed record MemoryFileEvolution
{
    public int AccessCount { get; set; }
    public int RelevantUseCount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    public double Relevance => AccessCount > 0 ? (double)RelevantUseCount / AccessCount : 1.0;

    public void RecordAccess(bool wasRelevant)
    {
        AccessCount++;
        if (wasRelevant) RelevantUseCount++;
        LastAccessedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// A MemoryFile is the filesystem-style persistent memory unit.
/// Organized by topic/project/context, persisted as .md files under memory/.
/// Analogous to Claude's Memory Files: user-browsable, topic-structured,
/// selectively loaded into context based on relevance to current task.
/// </summary>
public sealed record MemoryFile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "";
    public string? Topic { get; init; }
    public string Summary { get; init; } = "";
    public List<MemoryFileFact> Facts { get; init; } = new();
    public string Context { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public List<string> SourceEntityIds { get; init; } = new();
    public double Confidence { get; init; } = 0.85;
    public MemoryFileVerification Verification { get; init; } = new();
    public MemoryFileEvolution Evolution { get; init; } = new();
    public List<MemoryFileTrigger> Triggers { get; init; } = new();
    public string? SourceFile { get; set; }

    public bool IsActive => Evolution.Relevance >= 0.3 || Evolution.AccessCount < 5;
    public bool IsVerified => !Verification.IsStale;
    public bool IsReliable => Confidence >= 0.7 && IsVerified;
}
