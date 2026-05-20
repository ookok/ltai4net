namespace LTAI.MAF.Workflows;

public enum HandoffReason { Default, NeedResearch, NeedApproval, Escalate, FollowUp, UserQuestion }

public sealed class HandoffEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool OneWay { get; set; }

    public string ToToolDescription() => string.IsNullOrEmpty(Reason)
        ? $"Hand off to {To} agent"
        : $"Hand off to {To}: {Reason}";
}

public sealed class HandoffWorkflowBuilder
{
    private readonly Dictionary<string, Func<string, string, Task<string>>> _agents = new();
    private readonly List<HandoffEdge> _edges = new();
    private string _startAgent = "";
    private bool _returnToPrevious;
    private bool _emitStreamEvents;
    private bool _autonomousMode;

    public HandoffWorkflowBuilder WithStartAgent(string name, Func<string, string, Task<string>> fn)
    {
        _agents[name] = fn;
        _startAgent = name;
        return this;
    }

    public HandoffWorkflowBuilder SetStartAgent(string name)
    {
        _startAgent = name;
        return this;
    }

    public HandoffWorkflowBuilder WithAgent(string name, Func<string, string, Task<string>> fn)
    {
        _agents[name] = fn;
        return this;
    }

    public HandoffWorkflowBuilder WithHandoff(string from, string to, string? reason = null)
    {
        _edges.Add(new HandoffEdge { From = from, To = to, Reason = reason ?? "" });
        return this;
    }

    public HandoffWorkflowBuilder WithHandoffs(string from, string[] targets)
    {
        foreach (var t in targets)
            _edges.Add(new HandoffEdge { From = from, To = t });
        return this;
    }

    public HandoffWorkflowBuilder EnableReturnToPrevious()
    {
        _returnToPrevious = true;
        return this;
    }

    public HandoffWorkflowBuilder EnableAutonomousMode(int? maxTurns = null)
    {
        _autonomousMode = true;
        return this;
    }

    public HandoffWorkflowBuilder EmitStreamEvents()
    {
        _emitStreamEvents = true;
        return this;
    }

    public HandoffWorkflow Build()
    {
        return new HandoffWorkflow(_agents, _edges, _startAgent, _returnToPrevious, _autonomousMode, _emitStreamEvents);
    }
}

public sealed class HandoffWorkflow
{
    private readonly Dictionary<string, Func<string, string, Task<string>>> _agents;
    private readonly List<HandoffEdge> _edges;
    private readonly string _startAgent;
    private readonly bool _returnToPrevious;
    private readonly bool _autonomousMode;
    private readonly bool _emitStreamEvents;

    public HandoffWorkflow(
        Dictionary<string, Func<string, string, Task<string>>> agents,
        List<HandoffEdge> edges, string startAgent,
        bool returnToPrevious, bool autonomousMode, bool emitStreamEvents)
    {
        _agents = agents;
        _edges = edges;
        _startAgent = startAgent;
        _returnToPrevious = returnToPrevious;
        _autonomousMode = autonomousMode;
        _emitStreamEvents = emitStreamEvents;
    }

    public IEnumerable<string> GetOutboundTargets(string agent)
    {
        return _edges.Where(e => e.From == agent).Select(e => e.To);
    }

    public string? GetNextAgent(string current, string? userIntent = null)
    {
        if (!string.IsNullOrEmpty(userIntent))
        {
            foreach (var edge in _edges.Where(e => e.From == current))
            {
                if (userIntent.Contains(edge.To, StringComparison.OrdinalIgnoreCase))
                    return edge.To;
            }
        }
        return null;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string userInput)
    {
        var current = _startAgent;
        var turn = 0;
        const int maxTurns = 10;
        var conversation = new List<string> { $"User: {userInput}" };

        while (turn < maxTurns && _agents.ContainsKey(current))
        {
            var ctx = string.Join("\n", conversation.TakeLast(5));
            var result = await _agents[current](current, ctx);
            conversation.Add($"{current}: {result}");

            if (_emitStreamEvents)
                yield return $"event: agent_turn\ndata: {{\"agent\":\"{current}\",\"output\":\"{result[..Math.Min(100, result.Length)]}\"}}\n\n";

            var next = DetectHandoff(result, current);
            if (next == null) break;

            var handoffMsg = _edges.FirstOrDefault(e => e.From == current && e.To == next)?.ToToolDescription() ?? $"Handoff {current}→{next}";
            if (_emitStreamEvents)
                yield return $"event: handoff\ndata: {{\"from\":\"{current}\",\"to\":\"{next}\",\"reason\":\"{handoffMsg}\"}}\n\n";

            current = next;
            turn++;
        }

        if (_emitStreamEvents)
            yield return $"event: workflow_done\ndata: {{\"turns\":{turn}}}\n\n";
    }

    private string? DetectHandoff(string agentOutput, string currentAgent)
    {
        foreach (var edge in _edges.Where(e => e.From == currentAgent))
        {
            if (agentOutput.Contains($"handoff_to_{edge.To}", StringComparison.OrdinalIgnoreCase)
                || agentOutput.Contains($"[→{edge.To}]")
                || agentOutput.Contains($"handoff to {edge.To}", StringComparison.OrdinalIgnoreCase))
                return edge.To;
        }
        return null;
    }

    public Dictionary<string, object> GetGraph() => new()
    {
        ["start"] = _startAgent,
        ["nodes"] = _agents.Keys.Select(a => new { id = a, label = a }).ToList(),
        ["edges"] = _edges.Select(e => new { from = e.From, to = e.To, reason = string.IsNullOrEmpty(e.Reason) ? null : e.Reason }).ToList(),
        ["return_to_previous"] = _returnToPrevious,
        ["autonomous_mode"] = _autonomousMode,
        ["emit_events"] = _emitStreamEvents
    };
}
