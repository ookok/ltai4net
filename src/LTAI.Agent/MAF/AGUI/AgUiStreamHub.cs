using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.Agent.AGUI;

public sealed class AgUiEvent
{
    public string EventType { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Content { get; set; } = "";
    public Dictionary<string, object> Data { get; set; } = new();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class AgUiWorkflowState
{
    public string WorkflowId { get; set; } = "";
    public string Status { get; set; } = "running";
    public List<AgUiAgentState> Agents { get; set; } = new();
    public string CurrentAgentId { get; set; } = "";
    public List<string> HandoffHistory { get; set; } = new();
    public List<AgUiEvent> Events { get; set; } = new();
    public double Progress { get; set; }
}

public sealed class AgUiAgentState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string CurrentAction { get; set; } = "";
    public List<string> ToolCalls { get; set; } = new();
    public string Output { get; set; } = "";
    public double LatencyMs { get; set; }
}

public sealed class AgUiStreamHub
{
    private static readonly Lazy<AgUiStreamHub> _instance = new(() => new AgUiStreamHub());
    public static AgUiStreamHub Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, AgUiWorkflowState> _workflows = new();
    private readonly List<Func<AgUiEvent, Task>> _subscribers = new();
    private readonly object _subLock = new();

    private AgUiStreamHub() { }

    public AgUiWorkflowState StartWorkflow(string workflowId, List<(string id, string name, string role)> agents)
    {
        var state = new AgUiWorkflowState
        {
            WorkflowId = workflowId,
            Status = "running",
            Agents = agents.Select(a => new AgUiAgentState { Id = a.id, Name = a.name, Role = a.role, Status = "idle" }).ToList()
        };
        _workflows[workflowId] = state;
        Emit(new AgUiEvent { EventType = "workflow_start", AgentId = "system", AgentName = "System", Status = "started", Content = workflowId });
        return state;
    }

    public void AgentThinking(string workflowId, string agentId, string action)
    {
        if (!_workflows.TryGetValue(workflowId, out var wf)) return;
        wf.CurrentAgentId = agentId;
        var agent = wf.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent != null)
        {
            agent.Status = "thinking";
            agent.CurrentAction = action;
        }
        Emit(new AgUiEvent { EventType = "agent_thinking", AgentId = agentId, AgentName = agent?.Name ?? agentId, Status = "thinking", Content = action, Data = new() { ["workflow_id"] = workflowId } });
    }

    public void AgentActing(string workflowId, string agentId, string toolName)
    {
        if (!_workflows.TryGetValue(workflowId, out var wf)) return;
        var agent = wf.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent != null)
        {
            agent.Status = "acting";
            agent.ToolCalls.Add(toolName);
        }
        Emit(new AgUiEvent { EventType = "agent_acting", AgentId = agentId, AgentName = agent?.Name ?? agentId, Status = "acting", Content = toolName, Data = new() { ["workflow_id"] = workflowId, ["tool"] = toolName } });
    }

    public void AgentDone(string workflowId, string agentId, string output, double latencyMs)
    {
        if (!_workflows.TryGetValue(workflowId, out var wf)) return;
        var agent = wf.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent != null)
        {
            agent.Status = "done";
            agent.Output = output;
            agent.LatencyMs = latencyMs;
        }
        Emit(new AgUiEvent { EventType = "agent_done", AgentId = agentId, AgentName = agent?.Name ?? agentId, Status = "done", Content = output[..Math.Min(200, output.Length)], Data = new() { ["workflow_id"] = workflowId, ["latency_ms"] = latencyMs } });
    }

    public void Handoff(string workflowId, string fromAgent, string toAgent, string reason)
    {
        if (!_workflows.TryGetValue(workflowId, out var wf)) return;
        wf.HandoffHistory.Add($"{fromAgent}→{toAgent}: {reason}");
        Emit(new AgUiEvent { EventType = "handoff", AgentId = fromAgent, AgentName = fromAgent, Status = "handoff", Content = reason, Data = new() { ["workflow_id"] = workflowId, ["to"] = toAgent, ["reason"] = reason } });
    }

    public void WorkflowDone(string workflowId, string status = "completed")
    {
        if (!_workflows.TryGetValue(workflowId, out var wf)) return;
        wf.Status = status;
        wf.Progress = 1.0;
        Emit(new AgUiEvent { EventType = "workflow_done", AgentId = "system", AgentName = "System", Status = status, Content = workflowId });
    }

    public void Subscribe(Func<AgUiEvent, Task> handler)
    {
        lock (_subLock) _subscribers.Add(handler);
    }

    private void Emit(AgUiEvent evt)
    {
        lock (_subLock)
        {
            foreach (var sub in _subscribers)
                _ = sub(evt);
        }
    }

    public AgUiWorkflowState? GetState(string workflowId) =>
        _workflows.TryGetValue(workflowId, out var s) ? s : null;

    public string RenderSseEvent(AgUiEvent evt)
    {
        var data = JsonSerializer.Serialize(new
        {
            type = evt.EventType,
            agentId = evt.AgentId,
            agentName = evt.AgentName,
            status = evt.Status,
            content = evt.Content,
            data = evt.Data,
            ts = evt.Timestamp
        });
        return $"event: {evt.EventType}\ndata: {data}\n\n";
    }
}
