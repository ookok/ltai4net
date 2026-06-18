using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Delegation;

namespace LTAI.Agent.Tools;

/// <summary>
/// DeLM-inspired Decentralized Agent Tools (arXiv 2606.10662).
/// Enables agents to claim subtasks from a shared queue, read accumulated
/// verified context, and write back compact verified updates — all without
/// a central orchestrator.
///
/// Usage pattern:
///   1. Main agent enqueues a task with EnqueueDelegationTask
///   2. Specialist agents call ClaimNextTask to pick up matching work
///   3. Before working, call ReadVerifiedContext to see what's been done
///   4. After working, call WriteVerifiedUpdate to share results
///   5. Terminal updates use &lt;verified&gt; tags to mark completion
/// </summary>
[ToolDomain("delegation")]
public sealed class DelegationTools
{
    private readonly DelegationContext _ctx;

    public DelegationTools(DelegationContext ctx)
    {
        _ctx = ctx;
    }

    [Description("DeLM: 向共享任务队列提交一个可被其他 agent 认领的任务。\n"
        + "适用场景：需要其他 agent 协助完成的子任务、可以并行执行的分析工作。\n"
        + "参数：description — 任务描述；requiredSkills — 逗号分隔的技能关键词。\n"
        + "返回任务 ID，其他 agent 可通过 ClaimNextTask 认领。")]
    public string EnqueueDelegationTask(
        [Description("任务描述，应包含足够上下文")] string description,
        [Description("所需技能，逗号分隔，如 'code,security,sql'")] string requiredSkills)
    {
        var id = _ctx.EnqueueTask(description, requiredSkills);
        return $"Delegation task #{id} enqueued (skills: {requiredSkills}).";
    }

    [Description("DeLM: 认领下一个匹配当前 agent 技能的待处理任务。\n"
        + "适用场景：agent 启动时查找可做的工作、空闲时主动认领任务。\n"
        + "参数：agentName — 当前 agent 名称；skills — 逗号分隔的技能列表。\n"
        + "返回任务详情，或提示无匹配任务。")]
    public string ClaimNextTask(
        [Description("Agent 名称，如 LTAI-Security")] string agentName,
        [Description("技能列表，逗号分隔，如 'security,review'")] string skills)
    {
        var skillArray = skills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var task = _ctx.ClaimNext(agentName, skillArray);
        if (task == null)
            return "No matching delegation tasks available.";
        return $"Claimed task #{task.Id}: {task.Description}";
    }

    [Description("DeLM: 向已认领的任务写入一条紧凑已验证更新。\n"
        + "适用场景：完成阶段性工作后回写结果、分享发现供协作 agent 使用。\n"
        + "如果内容包含 &lt;verified&gt; 标签，该任务标记为已完成。\n"
        + "内容应使用紧凑格式：<citation>、<file-list> 或纯文本摘要。")]
    public string WriteVerifiedUpdate(
        [Description("任务 ID")] string taskId,
        [Description("当前 agent 名称")] string agentName,
        [Description("紧凑格式的验证更新内容")] string content)
    {
        var update = _ctx.WriteVerifiedUpdate(taskId, agentName, content);
        if (update == null)
            return $"Task #{taskId} not found or not claimed by you.";
        return $"Verified update written to task #{taskId}.";
    }

    [Description("DeLM: 读取任务的累积已验证上下文。\n"
        + "适用场景：认领任务后先查看已有进度，避免重复工作。\n"
        + "返回所有 agent 对该任务的已验证更新摘要。")]
    public string ReadVerifiedContext(
        [Description("任务 ID")] string taskId)
    {
        return _ctx.FormatVerifiedContext(taskId);
    }

    [Description("DeLM: 列出所有任务，可选按状态过滤。\n"
        + "适用场景：查看任务队列状态、监控进度。\n"
        + "参数：status — 可选过滤：pending/claimed/verified/failed")]
    public string ListDelegationTasks(
        [Description("状态过滤（可选）：pending/claimed/verified/failed")] string? status = null)
    {
        var tasks = _ctx.ListTasks(status);
        if (tasks.Count == 0) return "No delegation tasks.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Delegation Tasks\n");
        sb.AppendLine("| ID | Description | Skills | Status | Claimed By |");
        sb.AppendLine("|----|-------------|--------|--------|------------|");
        foreach (var t in tasks)
        {
            var desc = t.Description.Length > 50 ? t.Description[..50] + "..." : t.Description;
            var by = t.ClaimedBy ?? "—";
            sb.AppendLine($"| {t.Id} | {Escape(desc)} | {Escape(t.RequiredSkills)} | {t.Status} | {by} |");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
