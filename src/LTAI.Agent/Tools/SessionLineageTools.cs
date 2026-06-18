using System.ComponentModel;
using System.Text;
using LTAI.AI;
using LTAI.Core.Session;

namespace LTAI.Agent.Tools;

/// <summary>
/// DeLM/OpenRath-inspired session lineage tools.
///
/// Enables forking sessions for parallel exploration, merging compatible
/// branches back, and visualizing the full session lineage tree.
///
/// All operations are persisted to <c>.livingtree/sessions/</c> on disk.
/// </summary>
[ToolDomain("session")]
public sealed class SessionLineageTools
{
    private readonly SessionManager _sm;

    public SessionLineageTools(SessionManager sm)
    {
        _sm = sm;
    }

    [Description("Fork: 从当前会话创建一个子分支。\n"
        + "子分支是父会话的精确消息副本，后续操作不会影响父会话。\n"
        + "适用场景：尝试不同的解决方案而不丢失原始上下文、并行探索多个方向。\n"
        + "参数：label — 分支说明（如 '尝试用正则重写'）。\n"
        + "返回子分支会话名称。")]
    public string ForkSession(
        [Description("分支说明标签")] string label)
    {
        var name = _sm.ForkSession(label);
        return $"✅ Forked: {name} (label: {label})\n当前会话已切换到子分支。用 /session switch <名称> 切回父会话。";
    }

    [Description("Merge: 合并两个会话。\n"
        + "将指定源会话的消息追加到当前会话末尾，中间以合并标记分隔。\n"
        + "适用场景：将并行分支的发现合并回主线程、汇总多个 agent 的分析结果。\n"
        + "参数：sourceName — 源会话名称（从 SessionGraph 查看）。\n"
        + "返回合并后的会话名称。")]
    public string MergeSessions(
        [Description("要合并的源会话名称")] string sourceName,
        [Description("合并说明标签（可选）")] string? label = null)
    {
        try
        {
            var name = _sm.MergeSessions(sourceName, label);
            return $"✅ Merged: {name}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("SessionGraph: 以树形图展示会话血缘关系。\n"
        + "显示所有 fork/merge 操作形成的会话家族树。\n"
        + "适用场景：了解当前会话的起源和分支结构、查看所有子分支。\n"
        + "参数：root — 根会话名称（可选，默认自动选择）。")]
    public string SessionGraph(
        [Description("根会话名称（可选）")] string? root = null)
    {
        var tree = _sm.GetSessionGraph(root);
        if (tree.Count == 0) return "No session lineage data.";

        var sb = new StringBuilder();
        sb.AppendLine("## Session Lineage Tree\n");
        sb.AppendLine("```");
        foreach (var node in tree)
        {
            var indent = new string(' ', node.Depth * 2);
            var marker = node.Depth == 0 ? "●" : "○";
            sb.AppendLine($"{indent}{marker} {node.Name}  ({node.Label})");
        }
        sb.AppendLine("```");
        sb.AppendLine($"\n{tree.Count} session(s) in lineage.");

        // Show current session
        var current = _sm.CurrentSession;
        if (current != null)
            sb.AppendLine($"\n当前会话: **{current}**");
        return sb.ToString();
    }

    [Description("列出所有子会话。\n"
        + "适用场景：查看当前会话有哪些 fork 分支未合并。")]
    public string ListChildSessions(
        [Description("父会话名称（可选，默认当前会话）")] string? parentId = null)
    {
        parentId ??= _sm.CurrentSession;
        if (parentId == null) return "No active session.";

        var children = _sm.ListChildSessions(parentId);
        if (children.Length == 0)
            return $"No child sessions for **{parentId}**.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Child Sessions of {parentId}\n");
        foreach (var child in children)
            sb.AppendLine($"- {child.Name}");
        return sb.ToString();
    }
}
