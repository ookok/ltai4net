// Tool RAG: 动态工具召回 AIContextProvider
// 运行时拦截工具列表，按用户消息语义检索 Top-K 个相关工具，
// 替代全量 80+ 工具注入。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// MAF AIContextProvider：在每次 agent 调用前按用户意图动态召回工具。
/// 工作流：
///   1. 首次调用时初始化 ToolRegistry（对所有已注册工具建索引）
///   2. 每次请求取用户最后一条消息，做语义检索取 Top-K（默认 8）
///   3. 保留兜底工具: ReadFileContent, ListTools, WebFetch, RunCommand
///   4. 替换 context.Tools 为召回的子集
///
/// 注册位置：AIContextProviders 列表的第一个（最先执行）。
/// </summary>
public sealed class ToolRetrievalProvider : AIContextProvider
{
    // 兜底工具：无论用户说什么，这些工具始终可用
    private static readonly HashSet<string> PinnedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFileContent", "ListTools", "ListFiles", "WebFetch", "WebSearch",
        "RunCommand", "ListDirectory", "DirectoryTree", "Glob",
    };
    // 始终排除的元工具（不暴露给普通对话）
    private static readonly HashSet<string> ExcludedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // ToolRegistry 内部使用，不暴露给 LLM
    };

    private const int DefaultTopK = 8;
    private static bool _initialized;

    public ToolRetrievalProvider() : base(null, null, null) { }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var existing = context.AIContext;
        if (existing?.Tools is null || !existing.Tools.Any())
            return ValueTask.FromResult(existing);

        // 首次调用：初始化 ToolRegistry
        if (!_initialized)
        {
            ToolRegistry.Initialize(existing.Tools.ToList());
            _initialized = true;
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[ToolRAG] Registered tools:");
            foreach (var t in existing.Tools)
                System.Diagnostics.Debug.WriteLine($"  {t.Name}  ({t.Description})");
#endif
        }

        // 取用户最后一条消息作为查询
        var query = GetUserQuery(context);
        var selectedTools = new List<AITool>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // 语义检索 Top-K
            var hits = ToolRegistry.SearchTopK(query, DefaultTopK);
            var hitNames = new HashSet<string>(hits.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);

            // 从原始工具列表中选出：命中的 + 兜底的
            foreach (var tool in existing.Tools)
            {
                var name = tool.Name ?? "";
                if (ExcludedTools.Contains(name)) continue;
                if (hitNames.Contains(name) || PinnedTools.Contains(name))
                    selectedTools.Add(tool);
            }
        }

        // 如果检索结果太少（不足 3 个），回退到 PinnedTools
        if (selectedTools.Count < 3)
        {
            selectedTools.Clear();
            foreach (var tool in existing.Tools)
            {
                var name = tool.Name ?? "";
                if (ExcludedTools.Contains(name)) continue;
                if (PinnedTools.Contains(name) || selectedTools.Count < DefaultTopK)
                    selectedTools.Add(tool);
            }
        }

        return ValueTask.FromResult(new AIContext
        {
            Instructions = existing.Instructions,
            Messages = existing.Messages,
            Tools = selectedTools,
        });
    }

    private static string GetUserQuery(InvokingContext context)
    {
        // 取会话中最后一条用户消息
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return "";

        // 从后往前找用户消息，可取最近 1-3 轮拼接
        var parts = new List<string>();
        foreach (var m in msgs.Reverse())
        {
            if (m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            {
                parts.Add(m.Text.Trim());
                if (parts.Count >= 2) break; // 取最近 2 轮
            }
        }
        parts.Reverse();
        return string.Join(" ", parts);
    }
}
