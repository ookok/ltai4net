using LTAI.Agent.Mcp;
using LTAI.Core.Caching;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    internal static void RegisterMcpTools(ToolSet tools, string name, IServiceProvider sp, LTAIOptions opts, bool canRead, bool canWrite, bool canExec)
    {
        var cache = sp.GetRequiredService<LTAICacheFactory>().GetOrCreate<string, Task<IReadOnlyList<AITool>>>("mcp-tools-" + name, new LTAICacheOptions
        {
            MaxEntries = 64,
            DefaultTtl = TimeSpan.FromMinutes(10)
        });
        var mcpFactory = sp.GetRequiredService<McpClientFactory>();

        if (!cache.TryGet(name, out var mcpTask))
        {
            mcpTask = mcpFactory.GetToolsAsync(opts.Mcp);
            cache.Set(name, mcpTask);
        }

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
