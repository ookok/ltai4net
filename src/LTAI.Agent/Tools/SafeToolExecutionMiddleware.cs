using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class SafeToolExecutionMiddleware
{
    private readonly Dictionary<string, AITool> _toolMap;

    public SafeToolExecutionMiddleware(IEnumerable<AITool> tools)
    {
        _toolMap = tools.Where(t => t.Name != null).ToDictionary(t => t.Name!, StringComparer.OrdinalIgnoreCase);
    }

    public (bool shouldSuppress, string? message) BeforeToolCall(string toolName, string arguments)
    {
        var resolvedName = toolName;
        if (!_toolMap.ContainsKey(toolName))
        {
            var match = ToolCallRepairer.FuzzyMatchToolName(toolName, _toolMap.Keys);
            if (match != null) resolvedName = match;
        }

        return ToolCallRepairer.DetectToolLoop(resolvedName, arguments);
    }
}
