// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  WorkflowCheckpointingTests — MAF FileSystemJsonCheckpointStore
//  + LTAI CheckpointingExtensions 集成测试
// ═══════════════════════════════════════════════════════════════

using System.IO;
using Xunit;
using LTAI.Agent.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;

namespace LTAI.Tests.Workflows;

public class WorkflowCheckpointingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ltai-checkpoints-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static Workflow BuildSimpleWorkflow(string startExecutorId = "start")
    {
        var executor = new FunctionExecutor<string>(startExecutorId, (_, _, _) => { });
        return new WorkflowBuilder(startExecutorId)
            .BindExecutor(executor)
            .Build();
    }

    [Fact]
    public void FileSystemJsonCheckpointStore_CreateNewInstance_CreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-fs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(dir));
            Assert.NotNull(store);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CheckpointManager_CreateJson_WithFileSystemStore_ReturnsManager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-mgr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(dir));
            var manager = CheckpointManager.CreateJson(store);
            Assert.NotNull(manager);
            Assert.NotNull(CheckpointManager.Default);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WithFileSystemCheckpointing_NullOrEmptyPath_Throws()
    {
        var workflow = BuildSimpleWorkflow();
        Assert.Throws<ArgumentException>(() => workflow.WithFileSystemCheckpointing(""));
        Assert.Throws<ArgumentException>(() => workflow.WithFileSystemCheckpointing("   "));
    }

    [Fact]
    public void WithFileSystemCheckpointing_ValidPath_WrapsWorkflow()
    {
        var workflow = BuildSimpleWorkflow("ckpt-1");
        var enabled = workflow.WithFileSystemCheckpointing(_tempDir);

        Assert.NotNull(enabled);
        Assert.NotNull(enabled.Manager);
        Assert.NotNull(enabled.Store);
        Assert.NotNull(enabled.ExecutionEnvironment);
        Assert.Same(workflow, enabled.Inner);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void CheckpointEnabledWorkflow_AsAIAgent_ReturnsAIAgent()
    {
        var workflow = BuildSimpleWorkflow("ckpt-2");
        var enabled = workflow.WithFileSystemCheckpointing(_tempDir);

        var agent = enabled.AsAIAgent(name: "LTAI-Checkpointed");

        Assert.NotNull(agent);
        Assert.Equal("LTAI-Checkpointed", agent.Name);
    }

    [Fact]
    public async Task ListCheckpointsAsync_EmptyStore_ReturnsEmpty()
    {
        var workflow = BuildSimpleWorkflow("ckpt-3");
        var enabled = workflow.WithFileSystemCheckpointing(_tempDir);

        var checkpoints = await enabled.ListCheckpointsAsync("test-session-1");

        Assert.NotNull(checkpoints);
        Assert.Empty(checkpoints);
    }

    [Fact]
    public void InProcessExecution_OffThread_WithCheckpointing_ProducesEnvironment()
    {
        var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(_tempDir));
        var manager = CheckpointManager.CreateJson(store);

        var env = InProcessExecution.OffThread.WithCheckpointing(manager);

        Assert.NotNull(env);
    }
}
