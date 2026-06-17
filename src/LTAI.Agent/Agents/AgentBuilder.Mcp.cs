using System.Collections.Concurrent;
using LTAI.Agent.Mcp;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<AITool>>> s_mcpToolsCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void RegisterMcpTools(ToolSet tools, string name, IServiceProvider sp, LTAIOptions opts, bool canRead, bool canWrite, bool canExec)
    {
        var mcpFactory = sp.GetRequiredService<McpClientFactory>();
        var mcpTask = s_mcpToolsCache.GetOrAdd(name, _ => mcpFactory.GetToolsAsync(opts.Mcp));

        IReadOnlyList<AITool>? mcpTools;
        try
        {
            mcpTools = mcpTask.GetAwaiter().GetResult();
        }
        catch
        {
            return;
        }
        if (mcpTools == null) return;
        foreach (var mcpTool in mcpTools)
        {
            if (!canRead) continue;
            var mn = mcpTool.Name.ToLowerInvariant();
            if (mn.Contains("write") || mn.Contains("create") || mn.Contains("delete") || mn.Contains("upload"))
            { if (!canWrite) continue; }
            if (mn.Contains("shell") || mn.Contains("command") || mn.Contains("exec") || mn.Contains("process"))
            { if (!canExec) continue; }
            tools.Add(mcpTool, ToolPriority.External);
        }
    }
}
