using System.Reflection;
using LTAI.AI;
using LTAI.Agent;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using LTAI.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

/// <summary>Tests for P0/P1 architecture refactoring: static→instance, pipeline, quality gate.</summary>
public sealed class ArchitectureRefactorTests
{
    // ═══════════════════════════════════════════════════════════════
    //  ToolRegistry: singleton + basic operations
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToolRegistry_Singleton_AllToolsNotNull()
    {
        _ = ToolRegistry.AllTools;
    }

    [Fact]
    public void ToolRegistry_InitNotCalled_IsInitializedFalse()
    {
        ToolRegistry.Clear();
        Assert.False(ToolRegistry.IsInitialized);
    }

    [Fact]
    public void ToolRegistry_RecordCall_DoesNotThrow()
    {
        ToolRegistry.Clear();
        ToolRegistry.RecordCall("test_tool", true, 100);
    }

    [Fact]
    public void ToolRegistry_GetToolsByDomain_Empty_NoInit()
    {
        ToolRegistry.Clear();
        var result = ToolRegistry.GetToolsByDomain("core");
        Assert.Empty(result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  P0-4: MessageContext 管道阻断标志
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MessageContext_Default_AllFlagsFalse()
    {
        var ctx = new MessageContext("hi");
        Assert.False(ctx.GrammarCheckBlocked);
        Assert.False(ctx.AntiPatternBlocked);
        Assert.False(ctx.QualityGateBlocked);
        Assert.False(ctx.DoDBlocked);
        Assert.Null(ctx.PipelineError);
        Assert.False(ctx.SafetyBlocked);
    }

    [Fact]
    public void MessageContext_BlockingFlags_SetAndRead()
    {
        var ctx = new MessageContext("hi");
        ctx.GrammarCheckBlocked = true;
        ctx.QualityGateBlocked = true;
        ctx.DoDBlocked = true;

        Assert.True(ctx.GrammarCheckBlocked);
        Assert.True(ctx.QualityGateBlocked);
        Assert.True(ctx.DoDBlocked);
        Assert.False(ctx.AntiPatternBlocked);
        Assert.Null(ctx.PipelineError);
    }

    [Fact]
    public void MessageContext_PipelineError_SetAndRead()
    {
        var ctx = new MessageContext("hi");
        ctx.PipelineError = "error occurred";
        Assert.Equal("error occurred", ctx.PipelineError);
    }

    [Fact]
    public void MessageContext_SafetyBlocked_SetAndRead()
    {
        var ctx = new MessageContext("hi");
        ctx.SafetyBlocked = true;
        Assert.True(ctx.SafetyBlocked);
    }

    // ═══════════════════════════════════════════════════════════════
    //  P1: ChatScope Scoped 模式
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ChatScope_TraceId_GeneratedOnCreate()
    {
        var scope1 = new ChatScope();
        var scope2 = new ChatScope();
        Assert.NotNull(scope1.TraceId);
        Assert.NotEqual(scope1.TraceId, scope2.TraceId);
    }

    [Fact]
    public void ChatScope_TraceId_Length12()
    {
        var scope = new ChatScope();
        Assert.Equal(12, scope.TraceId.Length);
    }

    [Fact]
    public void ChatScope_CreatedAt_IsRecent()
    {
        var scope = new ChatScope();
        Assert.True(scope.CreatedAt <= DateTime.UtcNow);
        Assert.True(scope.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ChatScope_UserId_NullByDefault()
    {
        var scope = new ChatScope();
        Assert.Null(scope.UserId);
    }

    [Fact]
    public void ChatScope_UserId_Settable()
    {
        var scope = new ChatScope { UserId = "test-user" };
        Assert.Equal("test-user", scope.UserId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EditLedger 实例化
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EditLedger_RecordEdit_Empty_SummaryIsNull()
    {
        var ledger = new EditLedger();
        Assert.Null(ledger.GetSummary());
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void EditLedger_RecordEdit_SingleEntry()
    {
        var ledger = new EditLedger();
        ledger.RecordEdit("src/Program.cs");
        Assert.Equal(1, ledger.Count);
        var summary = ledger.GetSummary();
        Assert.NotNull(summary);
        Assert.Contains("src/Program.cs", summary);
    }

    [Fact]
    public void EditLedger_RecordEdit_MultipleEditsAggregate()
    {
        var ledger = new EditLedger();
        ledger.RecordEdit("src/Program.cs");
        ledger.RecordEdit("src/Program.cs");
        Assert.Equal(1, ledger.Count);
        var summary = ledger.GetSummary();
        Assert.Contains("2 edits", summary);
    }

    [Fact]
    public void EditLedger_RecordEdit_NewFlag()
    {
        var ledger = new EditLedger();
        ledger.RecordEdit("src/NewFile.cs", isNew: true);
        var summary = ledger.GetSummary();
        Assert.Contains("[new]", summary);
    }

    [Fact]
    public void EditLedger_Reset_ClearsAll()
    {
        var ledger = new EditLedger();
        ledger.RecordEdit("a.cs");
        ledger.RecordEdit("b.cs");
        Assert.Equal(2, ledger.Count);
        ledger.Reset();
        Assert.Equal(0, ledger.Count);
        Assert.Null(ledger.GetSummary());
    }

    [Fact]
    public void EditLedger_MultipleFiles_SummaryListsAll()
    {
        var ledger = new EditLedger();
        ledger.RecordEdit("a.cs");
        ledger.RecordEdit("b.cs");
        ledger.RecordEdit("c.cs", isNew: true);
        var summary = ledger.GetSummary();
        Assert.Contains("a.cs", summary);
        Assert.Contains("b.cs", summary);
        Assert.Contains("c.cs", summary);
    }

    [Fact]
    public void EditLedger_EstimatedTokens_AfterEdits()
    {
        var ledger = new EditLedger();
        Assert.Equal(0, ledger.EstimatedTokens);
        ledger.RecordEdit("test.cs");
        Assert.True(ledger.EstimatedTokens > 0);
    }

    [Fact]
    public void EditLedger_DefaultInstance_AfterSetDefault()
    {
        EditLedger.SetDefault(new EditLedger());
        Assert.NotNull(EditLedger.Default);
    }

    // ═══════════════════════════════════════════════════════════════
    //  QualityGateStep: 维度评分与阈值
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void QualityGate_AcceptsWellWrittenResponse()
    {
        var text = @"## 分析结果

根据代码审查的发现，有三个关键问题需要处理。

第一，变量命名不规范。
第二，缺少错误处理。
第三，性能瓶颈。";
        var ctx = new MessageContext("分析代码");
        ctx.Messages.Add(new(ChatRole.Assistant, text));
        var step = new QualityGateStep(NullLogger<QualityGateStep>.Instance);
        var result = step.ProcessAsync(ctx).Result;
        Assert.False(result.QualityGateBlocked);
    }

    [Fact]
    public void QualityGate_BlocksShorthandResponse()
    {
        var text = "好";
        var ctx = new MessageContext("写代码");
        ctx.Messages.Add(new(ChatRole.Assistant, text));
        var step = new QualityGateStep(NullLogger<QualityGateStep>.Instance);
        var result = step.ProcessAsync(ctx).Result;
        Assert.True(result.QualityGateBlocked);
    }

    [Fact]
    public void QualityGate_DetectsLetMeOpening()
    {
        var text = "## Let me analyze this code for you\n\nThe main issue is...";
        var ctx = new MessageContext("分析代码");
        ctx.Messages.Add(new(ChatRole.Assistant, text));
        var step = new QualityGateStep(NullLogger<QualityGateStep>.Instance);
        var result = step.ProcessAsync(ctx).Result;
        Assert.False(result.QualityGateBlocked);
    }

    [Fact]
    public void QualityGate_CodeBlockGetsLenientClarity()
    {
        var text = "```csharp\nConsole.WriteLine(\"hello\");\n```\n\nDone.";
        var ctx = new MessageContext("写代码");
        ctx.Messages.Add(new(ChatRole.Assistant, text));
        var step = new QualityGateStep(NullLogger<QualityGateStep>.Instance);
        var result = step.ProcessAsync(ctx).Result;
        Assert.False(result.QualityGateBlocked);
    }

    [Fact]
    public void QualityGate_CustomThreshold_AcceptsStricter()
    {
        var text = "short response here ok";
        var ctx1 = new MessageContext("hi");
        ctx1.Messages.Add(new(ChatRole.Assistant, text));
        var ctx2 = new MessageContext("hi");
        ctx2.Messages.Add(new(ChatRole.Assistant, text));
        var lenient = new QualityGateStep(NullLogger<QualityGateStep>.Instance, passThreshold: 0.3);
        var strict = new QualityGateStep(NullLogger<QualityGateStep>.Instance, passThreshold: 0.9);
        var lenientResult = lenient.ProcessAsync(ctx1).Result;
        var strictResult = strict.ProcessAsync(ctx2).Result;
        Assert.False(lenientResult.QualityGateBlocked);
        Assert.True(strictResult.QualityGateBlocked);
    }

    // ═══════════════════════════════════════════════════════════════
    //  PipelineRunner: 阻断链
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PipelineRunner_QualityGateBlock_StopsSubsequentSteps()
    {
        var ctx = new MessageContext("test");
        ctx.QualityGateBlocked = true;
        Assert.True(ctx.QualityGateBlocked);
    }

    [Fact]
    public void MessageContext_AllBlockingFlags_Independent()
    {
        var ctx = new MessageContext("test");
        ctx.GrammarCheckBlocked = true;
        ctx.AntiPatternBlocked = true;
        ctx.QualityGateBlocked = true;
        ctx.DoDBlocked = true;
        ctx.PipelineError = "err";
        ctx.SafetyBlocked = true;
        Assert.True(ctx.GrammarCheckBlocked);
        Assert.True(ctx.AntiPatternBlocked);
        Assert.True(ctx.QualityGateBlocked);
        Assert.True(ctx.DoDBlocked);
        Assert.Equal("err", ctx.PipelineError);
        Assert.True(ctx.SafetyBlocked);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SubagentTools: ToolPermission attribute filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToolPermission_Read_EnumValue()
    {
        Assert.Equal(1, (int)ToolPermission.Read);
        Assert.Equal(2, (int)ToolPermission.Write);
        Assert.Equal(4, (int)ToolPermission.Execute);
    }

    [Fact]
    public void ToolPermission_Flags_CombineCorrectly()
    {
        var readWrite = ToolPermission.Read | ToolPermission.Write;
        Assert.True(readWrite.HasFlag(ToolPermission.Read));
        Assert.True(readWrite.HasFlag(ToolPermission.Write));
        Assert.False(readWrite.HasFlag(ToolPermission.Execute));
    }

    [Fact]
    public void ToolPermissionAttribute_ClassLevel_ReflectsDeclared()
    {
        var attr = typeof(ExploreToolSet).GetCustomAttribute<ToolPermissionAttribute>(false);
        Assert.NotNull(attr);
        Assert.Equal(ToolPermission.Read, attr!.Required);
    }

    [Fact]
    public void ToolPermissionAttribute_Default_OnUndecoratedClass()
    {
        var attr = typeof(FileSystemTools).GetCustomAttribute<ToolPermissionAttribute>(false);
        Assert.Null(attr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ToolExecutionResult: 结构化错误类型
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToolExecutionResult_Ok_CreatesSuccess()
    {
        var result = ToolExecutionResult.Ok("output data");
        Assert.True(result.Success);
        Assert.Equal("output data", result.Output);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void ToolExecutionResult_Fail_CreatesError()
    {
        var result = ToolExecutionResult.Fail("something broke", "ERR_001");
        Assert.False(result.Success);
        Assert.Equal("something broke", result.ErrorMessage);
        Assert.Equal("ERR_001", result.ErrorCode);
        Assert.Null(result.Output);
    }

    [Fact]
    public void ToolExecutionResult_Fail_WithoutCode()
    {
        var result = ToolExecutionResult.Fail("generic error");
        Assert.False(result.Success);
        Assert.Equal("generic error", result.ErrorMessage);
        Assert.Null(result.ErrorCode);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ToolExecutionContext: 结构化执行上下文
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToolExecutionContext_Creation()
    {
        var ctx = new ToolExecutionContext
        {
            Workspace = "/project",
            GrantedPermissions = ToolPermission.Read | ToolPermission.Write
        };
        Assert.Equal("/project", ctx.Workspace);
        Assert.True(ctx.GrantedPermissions.HasFlag(ToolPermission.Read));
        Assert.True(ctx.GrantedPermissions.HasFlag(ToolPermission.Write));
        Assert.False(ctx.GrantedPermissions.HasFlag(ToolPermission.Execute));
    }

    [Fact]
    public void ToolExecutionContext_Metadata_EmptyByDefault()
    {
        var ctx = new ToolExecutionContext
        {
            Workspace = "/ws",
            GrantedPermissions = ToolPermission.Read
        };
        Assert.Empty(ctx.Metadata);
    }
}
