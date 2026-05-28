namespace LTAI.Models;

public enum MkToolType
{
    Shell,
    Http,
    Compose,
    Prompt,
    Service
}

public sealed record ToolParam
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "string";
    public string? Default { get; init; }
    public string Description { get; init; } = "";
    public bool Required { get; init; }
}

public sealed record MkToolEvolution
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int TotalUses { get; set; }
    public double SuccessRate => TotalUses > 0 ? (double)SuccessCount / TotalUses : 1.0;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

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

    public bool IsReliable => TotalUses >= 5 && SuccessRate >= 0.7;
}

public sealed record MkToolTrigger
{
    public string Pattern { get; init; } = "";
    public float Weight { get; set; } = 1.0f;
}

public sealed record ComposeStep
{
    public string Name { get; init; } = "";
    public string? ToolRef { get; init; }
    public string? InlineCommand { get; set; }
    public bool Parallel { get; init; }
    public Dictionary<string, string> Inputs { get; init; } = new();
}

public sealed record MkTool
{
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "general";
    public MkToolType Type { get; set; }
    public string Description { get; set; } = "";
    public List<ToolParam> Parameters { get; init; } = new();
    public string Template { get; set; } = "";
    public string HttpMethod { get; set; } = "GET";
    public string? HttpBody { get; set; }
    public List<string> HttpHeaders { get; init; } = new();
    public List<ComposeStep> Steps { get; init; } = new();
    public int TimeoutSec { get; set; } = 60;
    public int MaxOutputLines { get; set; } = 50;
    public string? ServiceName { get; set; }
    public string? ServiceMethod { get; set; }
    public List<MkToolTrigger> Triggers { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public MkToolEvolution Evolution { get; init; } = new();
    public string? SourceFile { get; set; }

    /// <summary>
    /// When true, this tool has no side effects and can be safely executed in parallel
    /// with other parallel-safe tools. Set for read-only operations (read_file, search, web_search).
    /// Default false — tools with side effects (write_file, shell, http mutate) remain serial.
    /// Adapted from DeepSeek-Reasonix Pillar 1 parallelSafe.
    /// </summary>
    public bool ParallelSafe { get; set; }

    public bool IsReliable => Evolution.IsReliable;

    public static MkTool Create(string name, MkToolType type, string description,
        string template = "", string domain = "general")
        => new()
        {
            Name = name,
            Type = type,
            Description = description,
            Template = template,
            Domain = domain
        };
}
