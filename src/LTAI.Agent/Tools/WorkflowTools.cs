using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Agent.Tools;

/// <summary>
/// Workflow tools with lazy DI resolution to avoid circular dependencies.
/// The WorkflowOrchestrator is resolved on first tool invocation, not at construction.
/// </summary>
public sealed class WorkflowTools
{
    private readonly IServiceProvider _sp;
    private Workflows.WorkflowOrchestrator? _wf;

    public WorkflowTools(IServiceProvider sp) => _sp = sp;

    private Workflows.WorkflowOrchestrator Wf =>
        _wf ??= _sp.GetRequiredService<Workflows.WorkflowOrchestrator>();

    [Description("Execute a handoff workflow: routes complex tasks to specialist agents (code, math, data, etc.)")]
    public async Task<string> WorkflowHandoff(
        [Description("Task description for agent orchestration")] string task)
    {
        var response = await Wf.ExecuteHandoffAsync(task);
        return response.Messages?.LastOrDefault()?.Text ?? "(no response)";
    }

    [Description("Execute agents in sequence, each receiving previous step's output")]
    public async Task<string> WorkflowSequential(
        [Description("Comma-separated agent names like 'LTAI-Code,LTAI-Math'")] string agentNames,
        [Description("Task to execute")] string task)
    {
        return await Wf.ExecuteSequentialAsync(
            agentNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            task);
    }

    [Description("Execute agents concurrently (in parallel), then combine results")]
    public async Task<string> WorkflowConcurrent(
        [Description("Comma-separated agent names like 'LTAI-Code,LTAI-Math'")] string agentNames,
        [Description("Task for each agent")] string task)
    {
        return await Wf.ExecuteConcurrentAsync(
            agentNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            task);
    }
}
