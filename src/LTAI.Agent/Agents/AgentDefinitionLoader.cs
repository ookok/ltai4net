using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

/// <summary>
/// Loads agent definitions from <c>agents/*.agent.md</c> YAML front-matter,
/// with a hardcoded fallback list when no files are present.
/// </summary>
internal static class AgentDefinitionLoader
{
    /// <summary>
    /// Flat record describing one agent definition. Replaces the inline
    /// Dictionary&lt;string, AIAgent&gt; building so each agent can be registered
    /// as a MAF keyed service via <c>AddAIAgent</c>.
    /// </summary>
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
        string[] Tools = null!)
    {
        public AIAgent? Build(IServiceProvider sp, string name)
        {
            try
            {
                // DI factory lambda is sync; Task.Run keeps the call non-blocking.
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
        // Try loading from agents/*.agent.md files first
        var fileDefs = AgentRegistry.LoadAll();
        if (fileDefs.Count > 0)
        {
            // First pass: build name lookup for inheritTools resolution
            var defByName = new Dictionary<string, AgentFileDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in fileDefs)
            {
                if (def.Name != null)
                    defByName[def.Name] = def;
            }

            // Second pass: yield with inheritTools resolved
            foreach (var def in fileDefs)
            {
                var key = def.Name?.ToLowerInvariant().Replace("ltai-", "") ?? "unknown";
                var tools = def.Tools;

                // Resolve inheritTools: parent name can be "chat" or "LTAI-Chat"
                if (!string.IsNullOrEmpty(def.InheritTools))
                {
                    var parentName = def.InheritTools;
                    if (!defByName.ContainsKey(parentName))
                        parentName = "LTAI-" + parentName;
                    if (defByName.TryGetValue(parentName, out var parent) && parent.Tools.Length > 0)
                    {
                        // Merge: inherit parent's tools, deduplicate, keep child's order
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
                    Tools: tools);
            }
            // Internal router agent (not from files) — used by AgentWorkflows for handoff routing
            yield return new("LTAI-Router", "任务调度器(无工具)", false, false, false, false, null, 0.3f, 0.95f, Prompt: null);
            yield break;
        }

        // Fallback: hardcoded defaults (no agents/*.agent.md files found)
        // 任务类型 → temperature/topP 参考：AI编程 0.3/0.95 | 工具调用 0.3/0.95 | 通用问答 0.8/0.95 | 数学推理 1.0/0.95
        yield return new("LTAI-Router",   "任务调度器(无工具)",      false, false, false, false, null, 0.3f, 0.95f);
        yield return new("LTAI-Chat",     "通用对话助手",          true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Chat-Pro", "深度推理助手(Pro)",      true,  true,  true,  true,  "l2", 0.3f, 0.95f);
        yield return new("LTAI-Code",     "代码分析助手",          true,  true,  true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-Math",     "数学计算助手",          false, false, false, true,  null, 1.0f, 0.95f);
        yield return new("LTAI-Data",     "数据处理助手",          true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-System",   "系统管理助手",          false, false, false, true,  null, 0.3f, 0.95f);
        yield return new("LTAI-LLM",      "纯对话助手",            false, false, false, false, null, 0.8f, 0.95f);
        yield return new("LTAI-Writer",   "创意写作助手",          true,  true,  true,  true,  null, 0.8f, 0.95f);
        yield return new("LTAI-Frontend", "前端网页开发助手",       true,  true,  true,  true,  null, 0.8f, 0.95f);
        yield return new("LTAI-DCI",      "直接语料交互助手(DCI)",   true,  false, true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Plan",     "架构规划师(只读)",       true,  false, true,  false, null, 0.5f, 0.95f);
        yield return new("LTAI-SQL",      "数据库查询助手",         true,  false, true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-API",      "API 开发与集成助手",     true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Arch",     "架构设计助手",           true,  false, true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-Test",     "测试编写与执行助手",     true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Review",   "代码审查助手",           true,  false, true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-Debug",    "调试诊断助手",           true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Security", "安全分析助手",           true,  true,  true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-DevOps",   "DevOps 运维助手",        true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Office",   "Office 文档处理助手",    true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-ScrumMaster", "Scrum Master 协调者", true,  false, true,  false, null, 0.2f, 0.9f);
        yield return new("LTAI-Explore", "仓库探索助手(FastContext)", true,  false, true,  false, null, 0.2f, 0.95f);
    }
}
