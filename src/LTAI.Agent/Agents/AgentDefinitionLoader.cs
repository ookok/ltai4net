using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

internal static class AgentDefinitionLoader
{
    public sealed record AgentDef(
        string Name,
        string Description,
        bool CanRead,
        bool CanWrite,
        bool CanList,
        bool CanExec,
        string? ModelId,
        float? Temperature,
        float? TopP,
        string? Prompt = null,
        string[] Tools = null!,
        string[]? Trigger = null,
        int TokenEstimate = 0)
    {
        public AIAgent? Build(IServiceProvider sp, string name)
        {
            try
            {
                return AgentBuilder.BuildAgentImpl(sp, name, Description, CanRead, CanWrite, CanList, CanExec,
                        modelId: ModelId, temperature: Temperature, topP: TopP,
                        agentPrompt: Prompt, yamlTools: Tools);
            }
            catch (Exception ex)
            {
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    ?.CreateLogger("LTAI.Agent.BuildAgent");
                logger?.LogError(ex, "Agent '{Name}' failed to build — skipping DI registration", name);
                return null;
            }
        }
    }

    public static IEnumerable<AgentDef> GetAgentDefinitions()
    {
        var fileDefs = AgentRegistry.LoadAll();
        if (fileDefs.Count > 0)
        {
            var defByName = new Dictionary<string, AgentFileDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in fileDefs)
            {
                if (def.Name != null)
                    defByName[def.Name] = def;
            }

            foreach (var def in fileDefs)
            {
                var key = def.Name?.ToLowerInvariant().Replace("ltai-", "") ?? "unknown";
                var tools = def.Tools;

                if (!string.IsNullOrEmpty(def.InheritTools))
                {
                    var parentName = def.InheritTools;
                    if (!defByName.ContainsKey(parentName))
                        parentName = "LTAI-" + parentName;
                    if (defByName.TryGetValue(parentName, out var parent) && parent.Tools.Length > 0)
                    {
                        var inherited = new HashSet<string>(parent.Tools, StringComparer.OrdinalIgnoreCase);
                        foreach (var t in tools)
                            inherited.Add(t);
                        tools = inherited.ToArray();
                    }
                }

                yield return new AgentDef(
                    Name: def.Name ?? key,
                    Description: def.Description,
            CanRead: def.Permissions.Contains("read", StringComparer.OrdinalIgnoreCase),
            CanWrite: def.Permissions.Contains("write", StringComparer.OrdinalIgnoreCase),
            CanList: def.Permissions.Contains("list", StringComparer.OrdinalIgnoreCase),
            CanExec: def.Permissions.Contains("exec", StringComparer.OrdinalIgnoreCase),
                    ModelId: def.ModelId,
                    Temperature: def.Temperature is >= -2 and <= 2 ? (float?)def.Temperature : null,
                    TopP: (float?)def.TopP,
                    Prompt: def.Prompt,
                    Tools: tools,
                    Trigger: def.Trigger.Length > 0 ? def.Trigger : null,
                    TokenEstimate: def.TokenEstimate);
            }
            yield return new("LTAI-Router", "任务调度器(无工具)", false, false, false, false, null, 0.3f, 0.95f, Prompt: null);
            yield break;
        }

        // Fallback: hardcoded defaults (no agents/*.agent.md files found)
        yield return new("LTAI-Router", "任务调度器(无工具)",      false, false, false, false, null, 0.3f, 0.95f);
        yield return new("LTAI-Chat",   "通用对话与规划助手",      true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Dev",    "全栈开发专家",            true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Data",   "数据处理与数据库专家",    true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-QA",     "质量保障专家",            true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Ops",    "运维安全专家",            true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Math",   "数学计算助手",            false, false, false, true,  null, 1.0f, 0.95f);
        yield return new("LTAI-System", "系统管理助手",            false, false, false, true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Writer", "创意写作助手",            true,  true,  true,  true,  null, 0.8f, 0.95f);
        yield return new("LTAI-Arch",   "架构审查与深度研究助手",  true,  false, true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Office", "Office 文档处理助手",     true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Explore","仓库探索助手(FastContext)",true, false, true,  false, null, 0.2f, 0.95f);
    }
}
