using LTAI.MAF.Workflows;

namespace LTAI.MAF.Agents;

public sealed class WorkflowRecipe
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Agents { get; set; } = Array.Empty<string>();
    public string Pattern { get; set; } = "Sequential";
}

public static class AgentOrchestrationRecipes
{
    public static readonly WorkflowRecipe SimpleChat = new()
    {
        Name = "Simple Chat",
        Description = "Triage → Memory → Executor: classify, retrieve context, execute",
        Agents = new[] { "Triage", "Memory", "Executor" },
        Pattern = "Sequential"
    };

    public static readonly WorkflowRecipe CodeReview = new()
    {
        Name = "Code Review Pipeline",
        Description = "CodeAgent generate → Critic review → Executor fix → QA verify",
        Agents = new[] { "CodeAgent", "Critic", "Executor", "QA" },
        Pattern = "Sequential"
    };

    public static readonly WorkflowRecipe ResearchReport = new()
    {
        Name = "Research Report",
        Description = "Planner → Research(multi-source) + Memory(recall) → DocAgent generate → QA",
        Agents = new[] { "Planner", "Research", "Memory", "DocAgent", "QA" },
        Pattern = "Handoff"
    };

    public static readonly WorkflowRecipe EIAReport = new()
    {
        Name = "EIA Report Generation",
        Description = "Triage → Memory(regulations) → Research(sites) → Planner → DocAgent → QA → Governance(compliance)",
        Agents = new[] { "Triage", "Memory", "Research", "Planner", "DocAgent", "QA", "Governance" },
        Pattern = "Sequential"
    };

    public static readonly WorkflowRecipe MultiPerspective = new()
    {
        Name = "Multi-Perspective Analysis",
        Description = "Research + CodeAgent + DocAgent → GroupChat → Critic → QA",
        Agents = new[] { "Research", "CodeAgent", "DocAgent", "Critic", "QA" },
        Pattern = "GroupChat"
    };

    public static readonly WorkflowRecipe SelfImproving = new()
    {
        Name = "Self-Improving Loop",
        Description = "Executor → Critic → Executor(fix) → QA → Reflection(learn) → Memory(store)",
        Agents = new[] { "Executor", "Critic", "QA", "Reflection", "Memory" },
        Pattern = "Sequential"
    };

    public static readonly WorkflowRecipe CustomerSupport = new()
    {
        Name = "Customer Support Handoff",
        Description = "Router → Triage → Memory → Executor. Router handoffs based on intent: billing→Executor, tech→CodeAgent, complaint→Governance",
        Agents = new[] { "Router", "Triage", "Memory", "Executor", "CodeAgent", "Governance" },
        Pattern = "Handoff"
    };

    public static readonly WorkflowRecipe ConcurrentResearch = new()
    {
        Name = "Concurrent Research",
        Description = "Planner → [Research, Memory, DocAgent] parallel → Executor synthesize",
        Agents = new[] { "Planner", "Research", "Memory", "DocAgent", "Executor" },
        Pattern = "Concurrent"
    };

    public static List<WorkflowRecipe> GetAllRecipes() => new()
    {
        SimpleChat, CodeReview, ResearchReport, EIAReport,
        MultiPerspective, SelfImproving, CustomerSupport, ConcurrentResearch
    };

    public static HandoffWorkflowBuilder BuildHandoffRecipe(WorkflowRecipe recipe)
    {
        var builder = new HandoffWorkflowBuilder();
        foreach (var agent in recipe.Agents)
        {
            var def = AgentCatalog.Get(agent);
            builder.WithAgent(agent, async (name, input) =>
            {
                await Task.CompletedTask;
                return $"[{def.Tier}] {name}: processed {input[..Math.Min(50, input.Length)]}";
            });
        }

        if (recipe.Agents.Length > 0)
            builder.SetStartAgent(recipe.Agents[0]);

        for (var i = 0; i < recipe.Agents.Length - 1; i++)
            builder.WithHandoff(recipe.Agents[i], recipe.Agents[i + 1]);

        return builder.EnableReturnToPrevious().EmitStreamEvents();
    }

    public static Dictionary<string, object> GetRecipeSummary(WorkflowRecipe recipe) => new()
    {
        ["name"] = recipe.Name,
        ["description"] = recipe.Description,
        ["pattern"] = recipe.Pattern,
        ["agents"] = recipe.Agents.Select(a =>
        {
            var def = AgentCatalog.Get(a);
            return new
            {
                name = a,
                tier = def.Tier,
                role = def.Role,
                tools = def.Tools,
                skills = def.Skills
            };
        }),
        ["agent_count"] = recipe.Agents.Length
    };
}
