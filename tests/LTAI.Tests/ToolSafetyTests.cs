using System.Reflection;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class ToolErrorRecoveryHandlerTests
{
    private static readonly IReadOnlyList<AITool> NoTools = Array.Empty<AITool>();

    [Fact]
    public void Recover_NotFound_NoFallback_ReturnsNotifyUser()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        var result = handler.Recover("nonexistent_tool", "", "not found");
        Assert.Equal(RecoveryAction.NotifyUser, result.Action);
    }

    [Fact]
    public void Recover_ExecutionFailed_ReturnsRetry()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        var result = handler.Recover("git", "commit -m 'fix'", "failed with exit code 128");
        Assert.Equal(RecoveryAction.Retry, result.Action);
    }

    [Fact]
    public void Recover_PermissionDenied_ReturnsNotifyUser()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        var result = handler.Recover("shell", "rm -rf /", "Permission denied");
        Assert.Equal(RecoveryAction.NotifyUser, result.Action);
    }

    [Fact]
    public void Recover_Timeout_ReturnsRetry()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        var result = handler.Recover("web", "fetch https://slow.example.com", "timed out");
        Assert.Equal(RecoveryAction.Retry, result.Action);
    }

    [Fact]
    public void Recover_ChineseErrorMessages_ClassifiedCorrectly()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        Assert.Equal(RecoveryAction.NotifyUser,
            handler.Recover("tool", "", "权限不足").Action);
        Assert.Equal(RecoveryAction.Retry,
            handler.Recover("tool", "", "超时").Action);
    }

    [Fact]
    public void Recover_ConsecutiveFailures_ReturnsAbort()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        Assert.Equal(RecoveryAction.Retry, handler.Recover("test_tool", "", "failed").Action);
        Assert.Equal(RecoveryAction.Retry, handler.Recover("test_tool", "", "failed").Action);
        Assert.Equal(RecoveryAction.Abort, handler.Recover("test_tool", "", "failed").Action);
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveCount()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        handler.Recover("test_tool", "", "failed");
        handler.RecordSuccess("test_tool");
        Assert.Equal(RecoveryAction.Retry, handler.Recover("test_tool", "", "failed").Action);
    }

    [Fact]
    public void Recover_UnknownError_DefaultsToRetry()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        Assert.Equal(RecoveryAction.Retry, handler.Recover("tool", "", "some unknown error occurred").Action);
    }

    [Fact]
    public void Recover_EmptyErrorMessage_ReturnsRetry()
    {
        var handler = new ToolErrorRecoveryHandler(NoTools,
            NullLogger<ToolErrorRecoveryHandler>.Instance);
        Assert.Equal(RecoveryAction.Retry, handler.Recover("tool", "", "").Action);
    }
}

public sealed class ToolSchemaAdapterTests
{
    [Fact]
    public void AdaptForModel_SkipProModel_ReturnsOriginal()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, "gpt-4-pro");
        Assert.Same(original, adapted);
    }

    [Fact]
    public void AdaptForModel_NullModelId_ReturnsOriginal()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, null!);
        Assert.Same(original, adapted);
    }

    [Fact]
    public void AdaptForModel_SkipMaxModel_ReturnsOriginal()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, "claude-3-opus-max");
        Assert.Same(original, adapted);
    }

    [Fact]
    public void AdaptForModel_SkipLargeModel_ReturnsOriginal()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, "llama-3-large");
        Assert.Same(original, adapted);
    }

    [Fact]
    public void AdaptForModel_ModelIdCaseInsensitive()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, "GPT-4-PRO");
        Assert.Same(original, adapted);
    }

    [Fact]
    public void AdaptForModel_TurboModel_ReturnsOriginal()
    {
        var adapter = new ToolSchemaAdapter();
        var original = new TestAITool();
        var adapted = adapter.AdaptForModel(original, "gpt-3.5-turbo");
        Assert.Same(original, adapted);
    }
}

public sealed class TestAITool : AITool;
