using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows.GraphIR;

public sealed class WorkflowCompiler
{
    private readonly IChatClient? _llm;
    private readonly ILogger<WorkflowCompiler> _logger;
    private readonly IReadOnlySet<string> _knownAgents;

    public bool PauseAfterEachStage { get; set; }

    public WorkflowCompiler(IChatClient? llm = null,
        ILogger<WorkflowCompiler>? logger = null,
        IReadOnlySet<string>? knownAgents = null)
    {
        _llm = llm;
        _logger = logger;
        _knownAgents = knownAgents ?? new HashSet<string>();
    }

    public async Task<WorkflowGraphIR> CompileAsync(string intent, CancellationToken ct = default)
    {
        _logger?.LogInformation("WorkflowCompiler: compiling '{Intent}'", intent);

        var roles = await AssignRolesAsync(intent, ct).ConfigureAwait(false);
        _logger?.LogInformation("Stage 1 complete: {Count} roles", roles.Count);

        var skeleton = await DesignStructureAsync(roles, intent, ct).ConfigureAwait(false);
        _logger?.LogInformation("Stage 2 complete: {NodeCount} nodes, {EdgeCount} edges",
            skeleton.Nodes.Count, skeleton.Edges.Count);

        var completed = await CompleteSemanticsAsync(skeleton, ct).ConfigureAwait(false);
        _logger?.LogInformation("Stage 3 complete: workflow '{Name}' ready", completed.Name);

        return completed;
    }

    public async Task<List<AgentRole>> AssignRolesAsync(string intent, CancellationToken ct = default)
    {
        if (_llm == null) return FallbackAssignRoles(intent);

        var knownList = _knownAgents.Count > 0
            ? $"可用 Agent: {string.Join(", ", _knownAgents)}"
            : "";

        var prompt = $@"
你是工作流编译器 Stage 1。分析以下自然语言意图，识别需要的 Agent 角色。
每个角色需包含：角色名称(name)、职责描述(description)、使用的工具(tools)。
{knownList}
只输出 JSON 数组，格式：[{{""name"": """", ""description"": """", ""tools"": []}}]

意图: {intent}";

        var response = await _llm.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
        var text = response.Messages?.LastOrDefault()?.Text ?? "[]";

        try
        {
            return JsonSerializer.Deserialize<List<AgentRole>>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkflowCompiler: AssignRoles JSON parse failed: {ex.Message}");
            return FallbackAssignRoles(intent);
        }
    }

    public async Task<WorkflowGraphIR> DesignStructureAsync(
        List<AgentRole> roles, string intent, CancellationToken ct = default)
    {
        var ir = new WorkflowGraphIR
        {
            Name = SanitizeName(intent),
            Metadata = { Description = intent }
        };

        if (_llm == null) return FallbackDesignStructure(roles, intent);

        var rolesJson = JsonSerializer.Serialize(roles);
        var prompt = $@"
你是工作流编译器 Stage 2。根据以下角色列表和意图，设计工作流图拓扑。
节点类型: Agent(Agent节点), Switch(条件分支), Loop(循环控制)
边类型: Control(控制流), Message(消息流), State(状态流)

只输出 JSON，格式：
{{""nodes"": [{{""id"": """", ""type"": ""Agent/Switch/Loop"", ""agentName"": """", ""config"": {{}}}}],
  ""edges"": [{{""from"": """", ""to"": """", ""type"": ""Control/Message/State"", ""condition"": """"}}]}}

角色: {rolesJson}
意图: {intent}";

        var response = await _llm.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
        var text = response.Messages?.LastOrDefault()?.Text ?? "";

        try
        {
            var graph = JsonSerializer.Deserialize<GraphStructure>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (graph != null)
            {
                ir.Nodes = graph.Nodes ?? [];
                ir.Edges = graph.Edges ?? [];
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WorkflowCompiler: DesignStructure JSON parse failed: {ex.Message}"); }

        if (ir.Nodes.Count == 0)
            ir = FallbackDesignStructure(roles, intent);

        return ir;
    }

    public async Task<WorkflowGraphIR> CompleteSemanticsAsync(
        WorkflowGraphIR skeleton, CancellationToken ct = default)
    {
        if (_llm == null) return skeleton;

        foreach (var node in skeleton.Nodes)
        {
            if (node.Type != GraphNodeType.Agent || string.IsNullOrEmpty(node.AgentName))
                continue;

            var prompt = $@"
你是工作流编译器 Stage 3。为以下 Agent 节点生成 prompt 模板和工具列表。
Agent: {node.AgentName}
工作流: {skeleton.Metadata.Description}

只输出 JSON：{{""prompt"": """", ""tools"": [""""]}}

要求:
- prompt 要清晰描述该 Agent 在此工作流中的职责
- tools 只包含该 Agent 需要的工具名称";

            var response = await _llm.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
            var text = response.Messages?.LastOrDefault()?.Text ?? "";

            try
            {
                var sem = JsonSerializer.Deserialize<NodeSemantics>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (sem != null)
                {
                    node.PromptTemplate = sem.Prompt ?? "";
                    node.Tools = sem.Tools ?? [];
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WorkflowCompiler: CompleteSemantics JSON parse failed for {node.Id}: {ex.Message}"); }
        }

        return skeleton;
    }

    // ── Fallback (no LLM) ──

    private List<AgentRole> FallbackAssignRoles(string intent)
    {
        var roles = new List<AgentRole>();
        var lowIntent = intent.ToLowerInvariant();

        if (lowIntent.Contains("规划") || lowIntent.Contains("plan") || lowIntent.Contains("设计") || lowIntent.Contains("design"))
            roles.Add(new AgentRole("planner", "任务规划与分解", ["web_search"]));
        if (lowIntent.Contains("编码") || lowIntent.Contains("code") || lowIntent.Contains("写") || lowIntent.Contains("实现") || lowIntent.Contains("implement"))
            roles.Add(new AgentRole("coder", "代码实现", ["code_interpreter", "file"]));
        if (lowIntent.Contains("审查") || lowIntent.Contains("review") || lowIntent.Contains("检查") || lowIntent.Contains("inspect"))
            roles.Add(new AgentRole("reviewer", "代码审查与质量检查", ["code_interpreter"]));
        if (lowIntent.Contains("分析") || lowIntent.Contains("analyze") || lowIntent.Contains("调研") || lowIntent.Contains("research"))
            roles.Add(new AgentRole("analyst", "需求分析与设计", ["web_search"]));
        if (lowIntent.Contains("测试") || lowIntent.Contains("test") || lowIntent.Contains("单元测试") || lowIntent.Contains("集成测试"))
            roles.Add(new AgentRole("tester", "测试编写与执行", ["run_command", "file"]));
        if (lowIntent.Contains("部署") || lowIntent.Contains("deploy") || lowIntent.Contains("发布") || lowIntent.Contains("release"))
            roles.Add(new AgentRole("devops", "部署与运维", ["run_command", "web_search"]));
        if (lowIntent.Contains("调试") || lowIntent.Contains("debug") || lowIntent.Contains("修复") || lowIntent.Contains("fix") || lowIntent.Contains("bug"))
            roles.Add(new AgentRole("debugger", "问题诊断与修复", ["code_interpreter", "run_command"]));
        if (lowIntent.Contains("文档") || lowIntent.Contains("doc") || lowIntent.Contains("注释") || lowIntent.Contains("readme"))
            roles.Add(new AgentRole("writer", "文档编写", ["file", "web_search"]));
        if (lowIntent.Contains("安全") || lowIntent.Contains("security") || lowIntent.Contains("漏洞") || lowIntent.Contains("vulnerability"))
            roles.Add(new AgentRole("security", "安全审查", ["web_search", "code_interpreter"]));
        if (lowIntent.Contains("搜索") || lowIntent.Contains("search") || lowIntent.Contains("查找") || lowIntent.Contains("find"))
            roles.Add(new AgentRole("searcher", "信息检索", ["web_search"]));

        if (roles.Count == 0)
            roles.Add(new AgentRole("assistant", "通用助手", []));

        return roles;
    }

    private WorkflowGraphIR FallbackDesignStructure(List<AgentRole> roles, string intent)
    {
        var ir = new WorkflowGraphIR
        {
            Name = SanitizeName(intent),
            Metadata = { Description = intent }
        };

        foreach (var role in roles)
        {
            ir.Nodes.Add(new GraphNode
            {
                Id = role.Name,
                Type = GraphNodeType.Agent,
                AgentName = role.Name,
                PromptTemplate = role.Description,
            });
        }

        for (int i = 1; i < ir.Nodes.Count; i++)
        {
            ir.Edges.Add(new GraphEdge
            {
                From = ir.Nodes[i - 1].Id,
                To = ir.Nodes[i].Id,
                Type = GraphEdgeType.Control,
            });
        }

        return ir;
    }

    private static string SanitizeName(string intent)
    {
        var name = intent.Length > 40 ? intent[..40] : intent;
        var sanitized = Regex.Replace(name, @"[^a-zA-Z0-9\u4e00-\u9fff_-]", "").Trim();
        return string.IsNullOrEmpty(sanitized) ? "workflow" : sanitized;
    }

    private sealed record GraphStructure(List<GraphNode>? Nodes, List<GraphEdge>? Edges);
    private sealed record NodeSemantics(string? Prompt, List<string>? Tools);
}

public sealed record AgentRole(string Name, string Description, List<string> Tools);
