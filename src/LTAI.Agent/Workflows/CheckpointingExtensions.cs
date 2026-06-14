// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CheckpointingExtensions — 启用 MAF Workflow 检查点/恢复
// ═══════════════════════════════════════════════════════════════
//
//  包装 LTAI 编排 workflow，自动启用 MAF 内置的检查点/恢复机制：
//  - InMemory: 进程内检查点（默认）
//  - FileSystem: JSON 文件持久化（跨进程恢复）
//
//  使用方法：
//    var orchestrator = ...;
//    var enabled = orchestrator.Workflow.WithFileSystemCheckpointing(".checkpoints");
//    var agent = enabled.AsAIAgent(name: "LTAI-Orchestrator");
//    // agent.RunAsync() 会自动写入检查点
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;

namespace LTAI.Agent.Workflows;

/// <summary>
/// 启用检查点的 Workflow 包装。
/// 暴露 <see cref="CheckpointManager"/>、<see cref="FileSystemJsonCheckpointStore"/>
/// 和 <see cref="ExecutionEnvironment"/>，便于上层应用执行 checkpoint 提交、查询、恢复等操作。
/// </summary>
public sealed class CheckpointEnabledWorkflow
{
    private readonly Workflow _inner;

    public Workflow Inner => _inner;
    public CheckpointManager Manager { get; }
    public FileSystemJsonCheckpointStore Store { get; }
    public InProcessExecutionEnvironment ExecutionEnvironment { get; }

    internal CheckpointEnabledWorkflow(Workflow inner, CheckpointManager manager, FileSystemJsonCheckpointStore store)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Manager = manager ?? throw new ArgumentNullException(nameof(manager));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        ExecutionEnvironment = InProcessExecution.OffThread.WithCheckpointing(manager);
    }

    /// <summary>
    /// 提升为启用检查点的 AIAgent。
    /// </summary>
    public AIAgent AsAIAgent(string? id = null, string? name = null, string? description = null)
    {
        return _inner.AsAIAgent(
            id: id,
            name: name,
            description: description,
            executionEnvironment: ExecutionEnvironment);
    }

    /// <summary>
    /// 列出当前 store 中所有 checkpoint 信息。
    /// </summary>
    public async Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(string sessionId)
    {
        var result = await Store.RetrieveIndexAsync(sessionId).ConfigureAwait(false);
        return result.ToList();
    }
}

public static class CheckpointingExtensions
{
    /// <summary>
    /// 使用文件系统 JSON CheckpointStore 包装 workflow，支持跨进程持久化。
    /// 检查点目录不存在时自动创建。
    /// </summary>
    /// <param name="workflow">要包装的 workflow</param>
    /// <param name="directoryPath">检查点 JSON 文件目录</param>
    /// <returns>启用检查点后的 workflow 包装</returns>
    public static CheckpointEnabledWorkflow WithFileSystemCheckpointing(this Workflow workflow, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Checkpoint directory path is required", nameof(directoryPath));

        var dir = new DirectoryInfo(directoryPath);
        if (!dir.Exists) dir.Create();
        var store = new FileSystemJsonCheckpointStore(dir);
        var manager = CheckpointManager.CreateJson(store);

        return new CheckpointEnabledWorkflow(workflow, manager, store);
    }
}
