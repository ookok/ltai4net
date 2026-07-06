using LTAI.Agent.Pipeline.Steps;

namespace LTAI.Agent.Pipeline;

/// <summary>
/// AsyncLocal ambient context that carries the current CompositionPlan
/// from PipelineRunner (pre-gen) to ToolFilteringChatClient (IChatClient middleware).
///
/// Set by ChatAgent after RunPreGenerationAsync, read by ToolFilteringChatClient
/// on each LLM sub-call to filter tools per DAG group.
/// </summary>
public static class CompositionPlanContext
{
    private static readonly AsyncLocal<CompositionPlan?> _current = new();

    public static CompositionPlan? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static void Reset()
    {
        _current.Value = null;
    }
}
