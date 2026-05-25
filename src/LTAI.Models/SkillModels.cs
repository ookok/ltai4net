namespace LTAI.Models;

/// <summary>
/// Skill layer: L0=atomic pattern, L1=task, L2=workflow, L3=domain, L4=meta.
/// Higher layers reference lower layers, never copy.
/// </summary>
public enum SkillLayer
{
    L0 = 0,
    L1 = 1,
    L2 = 2,
    L3 = 3,
    L4 = 4
}

public sealed record SkillTrigger
{
    public string Pattern { get; init; } = "";
    public float Weight { get; init; } = 1.0f;
}

public sealed record SkillStep
{
    public int Index { get; init; }
    public string Action { get; init; } = "";
    public string? SkillRef { get; init; }
    public string? ToolName { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
}

public sealed record SkillVerifyRule
{
    public string Description { get; init; } = "";
    public string? MustContain { get; init; }
    public string? MustNotContain { get; init; }
    public string? Pattern { get; init; }
}

public sealed record SkillEvolution
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int TotalUses { get; set; }
    public double SuccessRate => TotalUses > 0 ? (double)SuccessCount / TotalUses : 1.0;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? UpgradedFrom { get; set; }
    public int UpgradeGeneration { get; set; }

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
}

/// <summary>
/// A Skill is the atomic unit of intelligence. Encoded as .md file.
/// Only Skills are worth distributing. Everything else is local runtime detail.
/// </summary>
public sealed record Skill
{
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "";
    public SkillLayer Layer { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Intent { get; init; } = "";
    public List<SkillTrigger> Triggers { get; init; } = new();
    public List<string> Requires { get; init; } = new();
    public double Confidence { get; init; } = 0.85;
    public List<SkillStep> Steps { get; init; } = new();
    public List<SkillVerifyRule> Verification { get; init; } = new();
    public SkillEvolution Evolution { get; init; } = new();
    public string? SourceFile { get; set; }
    public string? Description { get; init; }
    public List<string> Tags { get; init; } = new();

    public bool IsActive => Evolution.SuccessRate >= 0.3 || Evolution.TotalUses < 5;
    public bool IsReliable => Evolution.SuccessRate >= 0.7 && Evolution.TotalUses >= 5;

    public string FullPath => $"{Domain}/{Name}";
    public string LayerDir => Layer switch
    {
        SkillLayer.L0 => "l0_atomic",
        SkillLayer.L1 => "l1_task",
        SkillLayer.L2 => "l2_workflow",
        SkillLayer.L3 => "l3_domain",
        SkillLayer.L4 => "l4_meta",
        _ => "unknown"
    };
}
