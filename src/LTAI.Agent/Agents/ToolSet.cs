using System.Collections;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Proactive uniqueness-guaranteed tool collection for agent construction.
///
/// Replaces the previous <c>List&lt;AITool&gt;</c> + post-hoc dedup loop pattern.
/// Uses a <see cref="Dictionary{String, AITool}"/> keyed by case-insensitive tool name,
/// so tool-name conflicts are resolved at insertion time rather than discovered later.
///
/// Priority rules (higher wins on name collision):
///   <see cref="ToolPriority.Core"/>      — LTAI native tools (filesystem, shell, search, ...)
///   <see cref="ToolPriority.Domain"/>    — domain-specific tools (git, docker, mcp, ...)
///   <see cref="ToolPriority.External"/>  — MCP/third-party tools
///
/// Usage in <c>BuildAgentImpl</c>:
/// <code>
/// var tools = new ToolSet();
/// RegisterFileAndTextTools(tools, ...);    // Core priority
/// RegisterMcpTools(tools, ...);            // External priority (loses to Core)
/// var list = tools.ToList();               // for ChatOptions.Tools
/// </code>
/// </summary>
public sealed class ToolSet : IEnumerable<AITool>
{
    private readonly Dictionary<string, AITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool. If a tool with the same name already exists at equal or higher
    /// priority, the new tool is silently dropped. If the existing tool has lower priority,
    /// it is replaced.
    /// </summary>
    /// <returns>true if the tool was added or replaced; false if it was dropped.</returns>
    public bool Add(AITool tool, ToolPriority priority = ToolPriority.Core)
    {
        var name = tool.Name ?? "";
        if (string.IsNullOrEmpty(name))
        {
            System.Diagnostics.Debug.WriteLine($"[ToolSet] Skipped tool with empty name");
            return false;
        }

        if (_tools.TryGetValue(name, out var existing))
        {
            var existingPriority = GetStoredPriority(name);
            if (priority > existingPriority)
            {
                _tools[name] = tool;
                SetStoredPriority(name, priority);
                return true;
            }
            // Same or lower priority — silently drop
            return false;
        }

        _tools[name] = tool;
        SetStoredPriority(name, priority);
        return true;
    }

    public int Count => _tools.Count;

    public List<AITool> ToList() => _tools.Values.ToList();

    public IEnumerator<AITool> GetEnumerator() => _tools.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Store priority alongside tool name (separate dict to avoid wrapper allocation)
    private readonly Dictionary<string, ToolPriority> _priorities = new(StringComparer.OrdinalIgnoreCase);

    private ToolPriority GetStoredPriority(string name)
        => _priorities.TryGetValue(name, out var p) ? p : ToolPriority.Core;

    private void SetStoredPriority(string name, ToolPriority priority)
        => _priorities[name] = priority;
}

/// <summary>
/// Priority tier for tool registration. Higher values win on name collision.
/// </summary>
public enum ToolPriority
{
    /// <summary>MCP and other external/third-party tools — lowest priority.</summary>
    External = 0,
    /// <summary>Domain-specific LTAI tools (git, docker, diagram, review, etc.).</summary>
    Domain = 1,
    /// <summary>Core LTAI tools (filesystem, shell, search, symbols, web, memory).</summary>
    Core = 2,
}
