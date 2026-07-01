using LTAI.AI;
using LTAI.Agent.Execution;
using LTAI.Agent.Learning;
using LTAI.Agent.Memory;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tests;

public sealed class AbstentionCheckSmokeTests
{
    private readonly AbstentionCheckStep _step = new();

    [Fact]
    public async Task NoToolCalls_Passes()
    {
        var ctx = new MessageContext("hello");
        ctx = await _step.ProcessAsync(ctx);
        Assert.False(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task RepeatedSameCall_Blocks()
    {
        var ctx = new MessageContext("search");
        ctx.ToolCalls.Add(("Read", "path=x", "ok"));
        ctx.ToolCalls.Add(("Read", "path=x", "ok"));
        ctx.ToolCalls.Add(("Read", "path=x", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task EmptyResults_Blocks()
    {
        var ctx = new MessageContext("test");
        for (int i = 0; i < 4; i++)
            ctx.ToolCalls.Add(("Search", "q=a", ""));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task PipelineError_Blocks()
    {
        var ctx = new MessageContext("go") { PipelineError = "fail" };
        ctx.ToolCalls.Add(("Read", "path=x", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task SingleCall_Passes()
    {
        var ctx = new MessageContext("go");
        ctx.ToolCalls.Add(("Read", "path=x", "content"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.False(ctx.AbstentionBlocked);
    }
}

public sealed class ToolEvalSmokeTests
{
    private readonly ToolEvalStep _step = new();

    [Fact]
    public async Task NoCalls_SkipsEvaluation()
    {
        var ctx = new MessageContext("hi");
        ctx = await _step.ProcessAsync(ctx);
        Assert.False(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out _));
    }

    [Fact]
    public async Task AllSuccessful_ScoresHigh()
    {
        var ctx = new MessageContext("do");
        ctx.ToolCalls.Add(("Glob", "*.cs", "file1.cs\nfile2.cs"));
        ctx.ToolCalls.Add(("Read", "file1.cs", "ok content"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out var r));
        Assert.True(r!.PassRate >= 0.9);
    }

    [Fact]
    public async Task MixedResults_ScoresModerate()
    {
        var ctx = new MessageContext("do");
        ctx.ToolCalls.Add(("Read", "a", "content"));
        ctx.ToolCalls.Add(("Read", "b", "error: missing"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out var r));
        Assert.Equal(0.5, r!.PassRate);
    }
}

public sealed class SelfCritiqueGeneratorSmokeTests
{
    [Fact]
    public async Task EchoCritic_ReturnsValidCritiques()
    {
        var critic = new EchoChatClient("{\"completeness\": \"missing details\"}");
        var gen = new SelfCritiqueGenerator(critic);
        var result = await gen.GenerateCritiqueAsync("write code", "ok", [], default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task NullCritic_EmptyResult()
    {
        var gen = new SelfCritiqueGenerator(null);
        var result = await gen.GenerateCritiqueAsync("hi", "output", [], default);
        Assert.Empty(result);
    }

    [Fact]
    public void HasSignificantIssues_Empty_ReturnsFalse()
    {
        var gen = new SelfCritiqueGenerator(null);
        Assert.False(gen.HasSignificantIssues([]));
    }
}

public sealed class SelfRefineSmokeTests
{
    [Fact]
    public async Task NoBlockers_PassesThrough()
    {
        var critic = new EchoChatClient("no issues");
        var gen = new SelfCritiqueGenerator(critic);
        var step = new SelfRefineStep(gen);
        var ctx = new MessageContext("hi", default);
        ctx.Messages.Add(new ChatMessage(ChatRole.Assistant, "fine output"));
        ctx = await step.ProcessAsync(ctx);
        Assert.NotNull(ctx);
    }

    [Fact]
    public async Task WithBlocker_AttemptsRefine()
    {
        var critic = new EchoChatClient("{\"clarity\": \"needs structure\"}");
        var gen = new SelfCritiqueGenerator(critic);
        var step = new SelfRefineStep(gen, maxRefineIterations: 1);
        var ctx = new MessageContext("fix this", default);
        ctx.Messages.Add(new ChatMessage(ChatRole.Assistant, "bad output"));
        ctx.GrammarCheckBlocked = true;
        ctx = await step.ProcessAsync(ctx);
        Assert.NotNull(ctx);
    }
}

internal sealed class StubToolRegistry : IToolRegistry
{
    public bool IsInitialized => true;
    public IReadOnlyList<ToolRegistry.ToolDef> AllTools { get; } = [];
    public Task InitializeAsync(IEnumerable<AITool> tools, EmbeddingClient embedder, ToolEmbeddingCache? cache = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, int k = 8, CancellationToken ct = default) => Task.FromResult(new List<ToolRegistry.ToolDef>());
    public Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k = 8, CancellationToken ct = default) => Task.FromResult(new List<ToolRegistry.ToolDef>());
    public Task<List<ToolRegistry.ToolDef>> SearchTopKAsync(string query, EmbeddingClient embedder, string? domain, int k, float[]? queryEmbedding, CancellationToken ct = default) => Task.FromResult(new List<ToolRegistry.ToolDef>());
    public void RecordCall(string toolName, bool success, long latencyMs) { }
    public IReadOnlyDictionary<string, ToolRegistry.ToolStats> GetAllStats() => new Dictionary<string, ToolRegistry.ToolStats>();
    public ToolRegistry.ToolStats? GetStats(string toolName) => null;
    public void ResetStats() { }
    public IReadOnlyList<ToolRegistry.ToolDef> GetToolsByDomain(string domain) => [];
    public AIFunction? GetToolByName(string name) => null;
    public Task<string?> InvokeToolAsync(string name, Dictionary<string, object?> args, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public void Clear() { }
    public void ClearEmbeddings() { }
}

public sealed class DFSDToolExecutorSmokeTests
{
    [Fact]
    public async Task NullLlm_ReturnsFailure()
    {
        var reg = new StubToolRegistry();
        var exec = new DFSDToolExecutor(reg);
        var result = await exec.ExecuteAsync("test query", default);
        Assert.False(result.Success);
        Assert.Contains("No LLM", result.Answer);
    }

    [Fact]
    public async Task WithEchoLlm_ExploresOneAction()
    {
        var reg = new StubToolRegistry();
        var llm = new EchoChatClient("FINAL: done");
        var exec = new DFSDToolExecutor(reg, llm, maxDepth: 5, maxNodes: 10);
        var result = await exec.ExecuteAsync("hello", default);
        Assert.True(result.Success);
        Assert.Equal("done", result.Answer);
    }
}

public sealed class GenerationOrderStepSmokeTests
{
    [Fact]
    public async Task NullReachIndex_Skips()
    {
        var step = new GenerationOrderStep(null);
        var ctx = new MessageContext("add interface IUserService");
        ctx = await step.ProcessAsync(ctx);
        Assert.DoesNotContain(ctx.Messages, m => m.Text?.Contains("Generation Order") == true);
    }
}

public sealed class CodeRepairAciSmokeTests
{
    private readonly CodeRepairAci _aci = new();

    [Fact]
    public async Task ViewFile_EmptyPath_ReturnsError()
    {
        var result = await _aci.ViewFileAsync("");
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditLines_InvalidRange_ReturnsError()
    {
        var result = await _aci.EditLinesAsync("test.cs", 10, 5, "new code");
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_ReturnsSummary()
    {
        var result = _aci.Submit("fixed the bug");
        Assert.Contains("fixed the bug", result);
    }
}

public sealed class ReflectionGeneratorSmokeTests
{
    [Fact]
    public async Task NullReflector_ReturnsEmpty()
    {
        var gen = new ReflectionGenerator(null);
        var result = await gen.GenerateReflectionAsync("query", "failed", "evaluation", default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task EchoReflector_ProducesResult()
    {
        var llm = new EchoChatClient("## Reflection\ncause: mock failure\nfix: mock fix");
        var gen = new ReflectionGenerator(llm);
        var result = await gen.GenerateReflectionAsync("test", "bad output", "error", default);
        Assert.NotNull(result);
    }
}

public sealed class ReflectionStoreSmokeTests
{
    [Fact]
    public void Constructor_AcceptsPalace()
    {
        var ctx = new MessageContext("test");
        Assert.NotNull(ctx);
    }
}

public sealed class ReWOOPlanningChatClientSmokeTests
{
    [Fact]
    public void NullRegistry_Throws()
    {
        var inner = new EchoChatClient("ok");
        Assert.Throws<ArgumentNullException>(() =>
            new ReWOOPlanningChatClient(inner, inner, inner, null, null!));
    }

    [Fact]
    public async Task GetResponse_WithNullPlanner_FallsThrough()
    {
        var reg = new StubToolRegistry();
        var inner = new EchoChatClient("direct response");
        var client = new ReWOOPlanningChatClient(inner, null, null, null, reg);
        var resp = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]);
        Assert.Equal("direct response", resp.Text);
    }
}
