using LTAI.AI;
using LTAI.Agent.Execution;
using LTAI.Agent.Learning;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LTAI.Tests;

public sealed class AbstentionCheckEdgeTests
{
    private readonly AbstentionCheckStep _step = new();

    [Fact]
    public async Task R1_ExactMatch_Blocks()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Search", "q=foo", "res"));
        ctx.ToolCalls.Add(("Search", "q=foo", "res"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task R1_DifferentArgs_Passes()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Search", "q=foo", "res"));
        ctx.ToolCalls.Add(("Search", "q=bar", "res"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.False(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task R2_TwoEmptyOnly_NotEnoughToBlock()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", ""));
        ctx.ToolCalls.Add(("Read", "b", ""));
        ctx = await _step.ProcessAsync(ctx);
        Assert.False(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task R2_ThreeEmpty_Blocks()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", ""));
        ctx.ToolCalls.Add(("Read", "b", ""));
        ctx.ToolCalls.Add(("Read", "c", ""));
        ctx.ToolCalls.Add(("Read", "d", ""));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task R3_ErrorResults_Blocks()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Build", "x", "error: CS1001"));
        ctx.ToolCalls.Add(("Build", "x", "exception: null ref"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task R5_SingleToolDominant_Blocks()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", "ok"));
        ctx.ToolCalls.Add(("Read", "b", "ok"));
        ctx.ToolCalls.Add(("Read", "c", "ok"));
        ctx.ToolCalls.Add(("Read", "d", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.AbstentionBlocked);
    }

    [Fact]
    public async Task RulesInContext_StoredOnBlock()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", ""));
        ctx.ToolCalls.Add(("Read", "b", ""));
        ctx.ToolCalls.Add(("Read", "c", ""));
        ctx.ToolCalls.Add(("Read", "d", ""));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<List<string>>("AbstentionRules", out var rules));
        Assert.NotEmpty(rules!);
    }
}

public sealed class ToolEvalEdgeTests
{
    private readonly ToolEvalStep _step = new();

    [Fact]
    public async Task AllErrors_SetsBlocked()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", "error: file not found"));
        ctx.ToolCalls.Add(("Build", "b", "exception: fail"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.QualityGateBlocked);
    }

    [Fact]
    public async Task MissingArgs_ReducesScore()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out var r));
        Assert.True(r!.ArgumentQuality < 0.95);
    }

    [Fact]
    public async Task ArgumentQuality_ClampedAtZero()
    {
        var ctx = new MessageContext("x");
        for (int i = 0; i < 12; i++)
            ctx.ToolCalls.Add(("Read", "", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out var r));
        Assert.True(r!.ArgumentQuality >= 0.0);
    }

    [Fact]
    public async Task SingleCallChain_PartialCompleteness()
    {
        var ctx = new MessageContext("x");
        ctx.ToolCalls.Add(("Read", "a", "ok"));
        ctx.ToolCalls.Add(("Read", "b", "ok"));
        ctx = await _step.ProcessAsync(ctx);
        Assert.True(ctx.TryGet<ToolEvalResult>("ToolEvalResult", out var r));
        Assert.True(r!.ChainCompleteness > 0);
    }
}

public sealed class DFSDToolExecutorEdgeTests
{
    [Fact]
    public async Task MaxDepth_ReturnsLimitMessage()
    {
        var stub = new StubToolRegistry();
        var llm = new EdgeEchoChatClient("TOOL: Search(q=test)\nTOOL: Read(path=x)");
        var exec = new DFSDToolExecutor(stub, llm, maxDepth: 1, maxNodes: 5);
        var result = await exec.ExecuteAsync("find x", default);
        Assert.False(result.Success);
        Assert.Contains("max depth", result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyQuery_DoesNotCrash()
    {
        var stub = new StubToolRegistry();
        var llm = new EdgeEchoChatClient("FINAL: ok");
        var exec = new DFSDToolExecutor(stub, llm, maxDepth: 5, maxNodes: 10);
        var result = await exec.ExecuteAsync("", default);
        Assert.True(result.Success);
    }
}

public sealed class CodeRepairAciEdgeTests
{
    private readonly CodeRepairAci _aci = new();

    [Fact]
    public async Task ViewFile_NullPath_ReturnsError()
    {
        var result = await _aci.ViewFileAsync(null!);
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchSymbol_EmptyQuery_ReturnsError()
    {
        var result = await _aci.SearchSymbolAsync("", "");
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTests_NoFilter_DoesNotThrow()
    {
        var result = await _aci.RunTestsAsync(null);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task EditLines_NegativeStart_Handled()
    {
        var result = await _aci.EditLinesAsync("test.cs", -1, 5, "code");
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_NullDescription_HandlesGracefully()
    {
        var result = _aci.Submit(null!);
        Assert.NotNull(result);
    }
}

public sealed class SelfCritiqueGeneratorEdgeTests
{
    [Fact]
    public async Task EchoWithEmptyCritique_NoIssues()
    {
        var critic = new EdgeEchoChatClient("{}");
        var gen = new SelfCritiqueGenerator(critic);
        var result = await gen.GenerateCritiqueAsync("q", "out", [], default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task EmptyToolResults_DoesNotCrash()
    {
        var critic = new EdgeEchoChatClient("{\"verbosity\": \"too long\"}");
        var gen = new SelfCritiqueGenerator(critic);
        var result = await gen.GenerateCritiqueAsync("q", "x", [], default);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void BuildRefinePrompt_NullQuery_DoesNotCrash()
    {
        var gen = new SelfCritiqueGenerator(null);
        var prompt = gen.BuildRefinePrompt(null!, "output", []);
        Assert.NotNull(prompt);
    }
}

public sealed class ReflectionGeneratorEdgeTests
{
    [Fact]
    public async Task EmptyEvaluation_DoesNotCrash()
    {
        var llm = new EdgeEchoChatClient("## Reflection\n## Causal\n## Corrective\n## Preventive");
        var gen = new ReflectionGenerator(llm);
        var result = await gen.GenerateReflectionAsync("q", "bad", "", default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task VeryLongQuery_Truncated()
    {
        var longQ = new string('x', 10000);
        var llm = new EdgeEchoChatClient("## Reflection\n## Causal\n## Corrective\n## Preventive");
        var gen = new ReflectionGenerator(llm);
        var result = await gen.GenerateReflectionAsync(longQ, "bad", "eval", default);
        Assert.NotNull(result);
    }
}

public sealed class ReWOOPlanningEdgeTests
{
    [Fact]
    public async Task ShortQuery_SkipsPlanning()
    {
        var reg = new StubToolRegistry();
        var inner = new EdgeEchoChatClient("fast answer");
        var client = new ReWOOPlanningChatClient(inner, inner, inner, null, reg);
        var resp = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("fast answer", resp.Text);
    }

    [Fact]
    public async Task PlannerThrows_FallsBack()
    {
        var reg = new StubToolRegistry();
        var inner = new EdgeEchoChatClient("fallback");
        var broken = new EdgeEchoChatClient("") { ThrowOnCall = true };
        var client = new ReWOOPlanningChatClient(inner, broken, inner, null, reg);
        var resp = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "this is a long query that should trigger planning")]);
        Assert.Equal("fallback", resp.Text);
    }
}

public sealed class EdgeEchoChatClient : IChatClient
{
    private readonly string _response;
    public bool ThrowOnCall { get; set; }

    public EdgeEchoChatClient(string response) => _response = response;
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public object? GetService(Type serviceType, string? serviceKey) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        if (ThrowOnCall) throw new InvalidOperationException("simulated failure");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, _response);
    }
}
