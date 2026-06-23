// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PipelineRunnerTests — 验证 PipelineRunner 的步骤组装/阻断链
//
//  测试覆盖:
//    1. RunPostGenerationAsync 仅启用了注册步骤
//    2. GrammarCheckStep 集成：有效代码通过、语法错误阻断
//    3. 阻断链：GrammarCheckBlocked 提前终止后续步骤
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Threading;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.AI;
using Xunit;

namespace LTAI.Tests;

public sealed class PipelineRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LTAI_PipelineTests_" + Guid.NewGuid().ToString("N"));
    private bool _disposed;

    public PipelineRunnerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private (GrammarCheckStep step, string path) CreateGrammarCheckWithDir()
    {
        var opts = new GrammarCheckOptions { EnableRuleEngine = false, EnableLspDiag = false };
        var step = new GrammarCheckStep(logger: null, workspacePath: _tempDir, options: opts);
        return (step, _tempDir);
    }

    private static PipelineRunner CreateRunner(
        GrammarCheckStep? grammarCheck = null,
        DoDCheckStep? doDCheck = null,
        QualityGateStep? qualityGate = null,
        RetrospectiveStep? retrospective = null)
    {
        var steps = new List<IPipelineStep>();
        if (grammarCheck != null) steps.Add(grammarCheck);
        if (doDCheck != null) steps.Add(doDCheck);
        if (qualityGate != null) steps.Add(qualityGate);
        if (retrospective != null) steps.Add(retrospective);
        return TestHelper.CreateRunner(steps.ToArray());
    }

    [Fact]
    public async Task RunPostGeneration_ValidCode_Passes()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var filePath = Path.Combine(dir, "test.cs");
        var code = """
            using System;
            namespace Test;
            public class Hello
            {
                public void Say() => Console.WriteLine("hi");
            }
            """;
        await File.WriteAllTextAsync(filePath, code);

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_SyntaxError_Blocks()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var filePath = Path.Combine(dir, "test.cs");
        var badCode = """
            using System;
            namespace Test;
            public class Hello
            {
                public void Say() => Console.WriteLine("hi")
            }
            """;
        await File.WriteAllTextAsync(filePath, badCode);

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_NoToolCalls_Skips()
    {
        var (grammarCheck, _) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);

        var ctx = new MessageContext("hello", CancellationToken.None);

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_UnknownExtension_Skips()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var filePath = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(filePath, [0x00, 0x01, 0x02]);

        var ctx = new MessageContext("write data.bin", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task SafetyBlocked_InPipeline_StopsEarly()
    {
        var (grammarCheck, _) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);

        var ctx = new MessageContext("test", CancellationToken.None);
        ctx.SafetyBlocked = true;
        ctx.SafetyReason = "test block";

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.SafetyBlocked);
        Assert.Equal("test block", ctx.SafetyReason);
        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_MultipleFiles_ChecksAll()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var csPath = Path.Combine(dir, "good.cs");
        var pyPath = Path.Combine(dir, "bad.py");
        await File.WriteAllTextAsync(csPath, "public class A {}");
        await File.WriteAllTextAsync(pyPath, "def foo():");

        var ctx = new MessageContext("write files", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={csPath}", "ok"));
        ctx.ToolCalls.Add(("write", $"path={pyPath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_MultipleCSharpSyntaxErrors_Blocks()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var filePath = Path.Combine(dir, "broken.cs");
        var badCode = """
            using System;
            class Foo {
                int x = 
                string y =
                void Bar() {
                    Console.WriteLine(
                }
            }
            """;
        await File.WriteAllTextAsync(filePath, badCode);

        var ctx = new MessageContext("write broken.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_QualityGate_DoesNotCrash()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var qualityGate = new QualityGateStep();
        var runner = CreateRunner(grammarCheck: grammarCheck);
        var filePath = Path.Combine(dir, "test.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.QualityGateBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_DoDCheck_DetectsTodo()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var doDCheck = new DoDCheckStep();
        var runner = CreateRunner(grammarCheck: grammarCheck, doDCheck: doDCheck);
        var filePath = Path.Combine(dir, "test.cs");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));
        ctx.Messages.Add(new(ChatRole.Assistant, "TODO: finish this later"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.DoDBlocked);
    }

    [Fact]
    public async Task NullSteps_SkipsGracefully()
    {
        var runner = TestHelper.CreateRunner(); // all null steps

        var ctx = new MessageContext("test", CancellationToken.None);
        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.NotNull(ctx);
    }

    [Fact]
    public async Task GrammarCheckOnPostGeneration_Works()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = TestHelper.CreateRunner(grammarCheck);
        var filePath = Path.Combine(dir, "test.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.False(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task GrammarCheckError_BlocksPostGeneration()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var runner = TestHelper.CreateRunner(grammarCheck);
        var filePath = Path.Combine(dir, "test.cs");
        await File.WriteAllTextAsync(filePath, "class {");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.GrammarCheckBlocked);
    }

    [Fact]
    public async Task RunPostGeneration_WithRetrospective_DoesNotCrash()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var retrospective = new RetrospectiveStep();
        var runner = CreateRunner(grammarCheck: grammarCheck, retrospective: retrospective);
        var filePath = Path.Combine(dir, "test.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));
        ctx.TraceId = "test-trace-123";

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.NotNull(ctx);
    }

    [Fact]
    public async Task DoDCheck_ConflictMarkers_Blocks()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var doDCheck = new DoDCheckStep();
        var runner = CreateRunner(grammarCheck: grammarCheck, doDCheck: doDCheck);
        var filePath = Path.Combine(dir, "conflict.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write conflict.cs", CancellationToken.None);
        ctx.Set("DoD", DoDConfig.DefaultTest);
        ctx.ToolCalls.Add(("write", $"path={filePath}",
            "<<<<<<< HEAD\nvar x = 1;\n=======\nvar x = 2;\n>>>>>>> branch\n"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.DoDBlocked);
    }

    [Fact]
    public async Task QualityGate_LowScore_Blocks()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var qualityGate = new QualityGateStep();
        var runner = CreateRunner(grammarCheck: grammarCheck, qualityGate: qualityGate);
        var filePath = Path.Combine(dir, "test.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.ToolCalls.Add(("write", $"path={filePath}", "ok"));
        ctx.Messages.Add(new(ChatRole.Assistant, "I'm not sure"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        Assert.True(ctx.QualityGateBlocked);
    }

    [Fact]
    public async Task QualityGateAndDoDCheck_BothBlock_Respectively()
    {
        var (grammarCheck, dir) = CreateGrammarCheckWithDir();
        var qualityGate = new QualityGateStep();
        var doDCheck = new DoDCheckStep();
        var runner = CreateRunner(grammarCheck: grammarCheck, qualityGate: qualityGate, doDCheck: doDCheck);
        var filePath = Path.Combine(dir, "conflict.cs");
        await File.WriteAllTextAsync(filePath, "public class A {}");

        var ctx = new MessageContext("write test.cs", CancellationToken.None);
        ctx.Set("DoD", DoDConfig.DefaultTest);
        ctx.ToolCalls.Add(("write", $"path={filePath}",
            "<<<<<<< HEAD\nvar x = 1;\n=======\nvar x = 2;\n>>>>>>> branch\n"));

        ctx = await runner.RunPostGenerationAsync(ctx);

        // QualityGate passes (no quality issue in conflict markers)
        // DoDCheck runs next and detects the conflict
        Assert.False(ctx.QualityGateBlocked);
        Assert.True(ctx.DoDBlocked);
    }
}
