namespace LTAI.Core.Configuration;

public sealed class PipelineConfig
{
    public PreStepEntry[] PreSteps { get; init; } = [];
    public PostStepGroup[] PostSteps { get; init; } = [];
}

public sealed class PreStepEntry
{
    public string Name { get; init; } = "";
    public int Order { get; init; }
}

public sealed class PostStepGroup
{
    public int Order { get; init; }
    public bool Parallel { get; init; }
    public bool AlwaysRun { get; init; }
    public string[] Names { get; init; } = [];
}
