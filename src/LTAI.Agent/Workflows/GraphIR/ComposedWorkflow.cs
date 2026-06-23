namespace LTAI.Agent.Workflows.GraphIR;

public sealed class ComposedWorkflowTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TemplateParameter> Parameters { get; set; } = [];
    public WorkflowGraphIR Graph { get; set; } = new();

    public WorkflowGraphIR Instantiate(Dictionary<string, string> args)
    {
        var ir = new WorkflowGraphIR
        {
            Name = Graph.Name,
            Metadata = new()
            {
                Description = Graph.Metadata?.Description ?? "",
                Version = Graph.Metadata?.Version ?? "1.0",
                Tags = Graph.Metadata?.Tags ?? [],
            }
        };

        foreach (var node in Graph.Nodes)
        {
            ir.Nodes.Add(new GraphNode
            {
                Id = ReplaceParams(node.Id, args),
                Type = node.Type,
                AgentName = ReplaceParams(node.AgentName, args),
                PromptTemplate = ReplaceParams(node.PromptTemplate, args),
                Tools = node.Tools.Select(t => ReplaceParams(t, args)).ToList(),
                Config = node.Config,
            });
        }

        foreach (var edge in Graph.Edges)
        {
            ir.Edges.Add(new GraphEdge
            {
                From = ReplaceParams(edge.From, args),
                To = ReplaceParams(edge.To, args),
                Type = edge.Type,
                Condition = edge.Condition != null ? ReplaceParams(edge.Condition, args) : null,
            });
        }

        return ir;
    }

    private static string ReplaceParams(string input, Dictionary<string, string> args)
    {
        foreach (var (key, value) in args)
            input = input.Replace($"{{{{.params.{key}}}}}", value);
        return input;
    }
}

public sealed class TemplateParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string? Default { get; set; }
    public string? Description { get; set; }
}

public sealed class ComposedWorkflowRegistry
{
    private readonly Dictionary<string, ComposedWorkflowTemplate> _templates = new();

    public IReadOnlyCollection<ComposedWorkflowTemplate> Templates => _templates.Values;

    public void Register(ComposedWorkflowTemplate template)
    {
        _templates[template.Name] = template;
    }

    public ComposedWorkflowTemplate? Get(string name) =>
        _templates.TryGetValue(name, out var t) ? t : null;

    public void LoadDefaults()
    {
        Register(CreateReviewRevise());
        Register(CreatePlanCodeReview());
        Register(CreateDebateConsensus());
        Register(CreateExploreExploit());
    }

    private static ComposedWorkflowTemplate CreateReviewRevise() => new()
    {
        Name = "review-revise",
        Description = "生成 → 评审 → 修改循环",
        Parameters =
        [
            new() { Name = "author_agent", Type = "agent_name", Default = "LTAI-Dev", Description = "生成 Agent" },
            new() { Name = "reviewer_agent", Type = "agent_name", Default = "LTAI-QA", Description = "评审 Agent" },
            new() { Name = "max_iterations", Type = "integer", Default = "3", Description = "最大迭代次数" },
        ],
        Graph = new WorkflowGraphIR
        {
            Name = "review-revise",
            Nodes =
            [
                new() { Id = "generate", Type = GraphNodeType.Agent, AgentName = "{{.params.author_agent}}" },
                new() { Id = "review", Type = GraphNodeType.Agent, AgentName = "{{.params.reviewer_agent}}" },
                new() { Id = "decide", Type = GraphNodeType.Switch, Config = new() { Condition = "review.approved || iterations >= {{.params.max_iterations}}" } },
                new() { Id = "end", Type = GraphNodeType.Agent, AgentName = "{{.params.author_agent}}" },
            ],
            Edges =
            [
                new() { From = "generate", To = "review", Type = GraphEdgeType.Control },
                new() { From = "review", To = "decide", Type = GraphEdgeType.Control },
                new() { From = "decide", To = "generate", Type = GraphEdgeType.Control, Condition = "!review.approved" },
                new() { From = "decide", To = "end", Type = GraphEdgeType.Control, Condition = "review.approved" },
            ],
        },
    };

    private static ComposedWorkflowTemplate CreatePlanCodeReview() => new()
    {
        Name = "plan-code-review",
        Description = "规划 → 编码 → 评审",
        Parameters =
        [
            new() { Name = "planner_agent", Type = "agent_name", Default = "LTAI-Arch", Description = "架构 Agent" },
            new() { Name = "coder_agent", Type = "agent_name", Default = "LTAI-Dev", Description = "编码 Agent" },
            new() { Name = "reviewer_agent", Type = "agent_name", Default = "LTAI-QA", Description = "评审 Agent" },
        ],
        Graph = new WorkflowGraphIR
        {
            Name = "plan-code-review",
            Nodes =
            [
                new() { Id = "plan", Type = GraphNodeType.Agent, AgentName = "{{.params.planner_agent}}" },
                new() { Id = "code", Type = GraphNodeType.Agent, AgentName = "{{.params.coder_agent}}" },
                new() { Id = "review", Type = GraphNodeType.Agent, AgentName = "{{.params.reviewer_agent}}" },
            ],
            Edges =
            [
                new() { From = "plan", To = "code", Type = GraphEdgeType.Control },
                new() { From = "code", To = "review", Type = GraphEdgeType.Control },
            ],
        },
    };

    private static ComposedWorkflowTemplate CreateDebateConsensus() => new()
    {
        Name = "debate-consensus",
        Description = "多 Agent 辩论 → 仲裁",
        Parameters =
        [
            new() { Name = "debater_agents", Type = "string", Default = "LTAI-Chat,LTAI-Data", Description = "辩论者(逗号分隔)" },
            new() { Name = "arbiter_agent", Type = "agent_name", Default = "LTAI-Chat", Description = "仲裁者" },
        ],
        Graph = new WorkflowGraphIR
        {
            Name = "debate-consensus",
            Nodes =
            [
                new() { Id = "debate1", Type = GraphNodeType.Agent, AgentName = "{{.params.debater_agents}}" },
                new() { Id = "debate2", Type = GraphNodeType.Agent, AgentName = "{{.params.debater_agents}}" },
                new() { Id = "arbiter", Type = GraphNodeType.Agent, AgentName = "{{.params.arbiter_agent}}" },
            ],
            Edges =
            [
                new() { From = "debate1", To = "arbiter", Type = GraphEdgeType.Message },
                new() { From = "debate2", To = "arbiter", Type = GraphEdgeType.Message },
            ],
        },
    };

    private static ComposedWorkflowTemplate CreateExploreExploit() => new()
    {
        Name = "explore-exploit",
        Description = "搜索 → 分析 → 决策",
        Parameters =
        [
            new() { Name = "explorer_agent", Type = "agent_name", Default = "LTAI-Chat", Description = "搜索 Agent" },
            new() { Name = "analyzer_agent", Type = "agent_name", Default = "LTAI-Data", Description = "分析 Agent" },
        ],
        Graph = new WorkflowGraphIR
        {
            Name = "explore-exploit",
            Nodes =
            [
                new() { Id = "explore", Type = GraphNodeType.Agent, AgentName = "{{.params.explorer_agent}}" },
                new() { Id = "analyze", Type = GraphNodeType.Agent, AgentName = "{{.params.analyzer_agent}}" },
                new() { Id = "decide", Type = GraphNodeType.Agent, AgentName = "{{.params.explorer_agent}}" },
            ],
            Edges =
            [
                new() { From = "explore", To = "analyze", Type = GraphEdgeType.Control },
                new() { From = "analyze", To = "decide", Type = GraphEdgeType.Control },
            ],
        },
    };
}
