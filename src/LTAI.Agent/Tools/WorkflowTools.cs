using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Workflow tools with lazy DI resolution to avoid circular dependencies.
/// The WorkflowOrchestrator is resolved on first tool invocation, not at construction.
/// </summary>
[ToolDomain("workflow")]
public sealed class WorkflowTools
{
    private readonly IServiceProvider _sp;
    private Workflows.WorkflowOrchestrator? _wf;

    public WorkflowTools(IServiceProvider sp) => _sp = sp;

    private Workflows.WorkflowOrchestrator Wf =>
        _wf ??= _sp.GetRequiredService<Workflows.WorkflowOrchestrator>();

    [Description("执行任务分配工作流：将复杂任务路由到 specialist 子 Agent（代码/架构/聊天/推理等）。\n"
        + "适用场景：需要专业知识才能回答的问题、跨领域复杂任务、自动选择最合适的子 Agent。\n"
        + "不适用场景：简单问题可以直接回答的、已明确指定某个子 Agent 的。\n"
        + "关键参数：task — 要分配给子 Agent 的任务描述。")]
    [ToolExample("帮我写一个 C# 排序算法（路由到代码 Agent）")]
    [ToolExample("分析这个项目的架构（路由到架构 Agent）")]
    public async Task<string> WorkflowHandoff(
        [Description("Task description for agent orchestration")] string task)
    {
        var response = await Wf.ExecuteHandoffAsync(task);
        return response.Messages?.LastOrDefault()?.Text ?? "(no response)";
    }

    [Description("按顺序依次执行多个 Agent，每个 Agent 接收上一步的输出。\n"
        + "适用场景：多步骤处理流水线、先分析再生成的串联任务。\n"
        + "不适用场景：需要 Agent 同时工作（请用 WorkflowConcurrent）。\n"
        + "关键参数：agentNames — Agent 名称数组；task — 初始任务。")]
    [ToolExample("先分析代码再生成报告")]
    public async Task<string> WorkflowSequential(
        [Description("Comma-separated agent names like 'LTAI-Code,LTAI-Math'")] string agentNames,
        [Description("Task to execute")] string task)
    {
        return await Wf.ExecuteSequentialAsync(
            agentNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            task);
    }

    [Description("同时并行执行多个 Agent，然后合并结果。\n"
        + "适用场景：需要多个角度同时分析问题、独立任务并行处理。\n"
        + "不适用场景：需要依赖前一步结果（请用 WorkflowSequential）。\n"
        + "关键参数：agentNames — Agent 名称数组；task — 要执行的任务。")]
    [ToolExample("同时用代码分析和架构分析审查这个项目")]
    public async Task<string> WorkflowConcurrent(
        [Description("Comma-separated agent names like 'LTAI-Code,LTAI-Math'")] string agentNames,
        [Description("Task for each agent")] string task)
    {
        return await Wf.ExecuteConcurrentAsync(
            agentNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            task);
    }
}
