namespace LTAI.Agent.Experts;

/// <summary>
/// Expert domain classification for MoE-style sparse activation routing.
/// </summary>
public enum ExpertDomain
{
    KG,
    CodeGraph,
    Document,
    Tool,
    Skill
}
