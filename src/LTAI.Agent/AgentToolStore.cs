using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Central tool registry that maps agent names to their <see cref="AITool"/> lists.
///
/// Aligns with MAF's keyed <c>AITool</c> DI pattern: tools are stored per-agent
/// and can be discovered by external consumers (DevUI, other agents, workflows)
/// without requiring the tools to be in <see cref="ChatOptions.Tools"/> at all times.
///
/// Registration happens during <see cref="AgentBuilder.BuildAgentImpl"/> after
/// all tools are assembled. Resolution is O(1) per agent name.
/// </summary>
public sealed class AgentToolStore
{
    private readonly ConcurrentDictionary<string, List<AITool>> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool for the specified agent. Idempotent per tool name
    /// (last registration wins per agent).
    /// </summary>
    public void Register(string agentName, AITool tool)
    {
        var list = _store.GetOrAdd(agentName, _ => new List<AITool>());
        lock (list)
        {
            var idx = list.FindIndex(t => string.Equals(t.Name, tool.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                list[idx] = tool;
            else
                list.Add(tool);
        }
    }

    /// <summary>
    /// Registers multiple tools for the specified agent.
    /// </summary>
    public void RegisterRange(string agentName, IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
            Register(agentName, tool);
    }

    /// <summary>
    /// Returns the tool list for the specified agent, or an empty list if none registered.
    /// </summary>
    public IReadOnlyList<AITool> GetTools(string agentName)
    {
        if (_store.TryGetValue(agentName, out var list))
        {
            lock (list)
                return list.ToArray();
        }
        return Array.Empty<AITool>();
    }

    /// <summary>
    /// Returns all agent names that have registered tools.
    /// </summary>
    public IEnumerable<string> GetAgentNames() => _store.Keys;

    /// <summary>
    /// Returns the number of tools registered for the specified agent.
    /// </summary>
    public int GetToolCount(string agentName)
        => _store.TryGetValue(agentName, out var list) ? list.Count : 0;
}
