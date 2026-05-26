namespace LTAI.Models;

public sealed record PromptVariable
{
    public string Name { get; init; } = "";
    public string? Default { get; init; }
    public string Description { get; init; } = "";
    public bool Required { get; init; }
}

public sealed record PromptEvolution
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int TotalUses { get; set; }
    public double SuccessRate => TotalUses > 0 ? (double)SuccessCount / TotalUses : 1.0;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
    public string? ImprovedFrom { get; set; }

    public void RecordSuccess()
    {
        SuccessCount++;
        TotalUses++;
        LastUsedAt = DateTime.UtcNow;
    }

    public void RecordFailure()
    {
        FailureCount++;
        TotalUses++;
        LastUsedAt = DateTime.UtcNow;
    }

    public bool IsReliable => SuccessRate >= 0.7 && TotalUses >= 5;
}

public sealed record PromptTrigger
{
    public string Pattern { get; init; } = "";
    public float Weight { get; set; } = 1.0f;
}

public sealed record PromptSection
{
    public string Name { get; init; } = "";
    public string? PromptId { get; init; }
    public int Order { get; init; }
    public bool Optional { get; init; }
}

public sealed record PromptFile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "general";
    public string Description { get; set; } = "";
    public string Template { get; set; } = "";
    public List<PromptVariable> Variables { get; init; } = new();
    public List<PromptTrigger> Triggers { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public PromptEvolution Evolution { get; init; } = new();
    public string? SourceFile { get; set; }

    public static PromptFile Create(string name, string domain, string template,
        List<PromptVariable>? variables = null)
        => new()
        {
            Name = name,
            Domain = domain,
            Template = template,
            Variables = variables ?? new()
        };
}

public sealed record PromptTemplate
{
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "general";
    public string Description { get; set; } = "";
    public List<PromptSection> Sections { get; init; } = new();
    public int MaxTotalChars { get; set; } = 8000;
    public PromptEvolution Evolution { get; init; } = new();
    public string? SourceFile { get; set; }
}

public sealed record PromptRenderResult
{
    public string PromptId { get; init; } = "";
    public string Rendered { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<string> MissingVariables { get; init; } = new();
}

public sealed record PromptVariantGroup
{
    public string GroupId { get; init; } = "";
    public string Domain { get; init; } = "general";
    public List<string> VariantIds { get; init; } = new();
    public string Algorithm { get; init; } = "epsilon-greedy";
    public double ExplorationRate { get; init; } = 0.1;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public enum AbTestAlgorithm
{
    EpsilonGreedy,
    ThompsonSampling,
    Ucb1
}

public sealed record AbTestResult
{
    public string GroupId { get; init; } = "";
    public string SelectedVariantId { get; init; } = "";
    public List<VariantScore> AllScores { get; init; } = new();
    public string Algorithm { get; init; } = "";
}

public sealed record VariantScore
{
    public string VariantId { get; init; } = "";
    public double Score { get; init; }
    public double SuccessRate { get; init; }
    public int TotalUses { get; init; }
    public string? Rendered { get; init; }
}
