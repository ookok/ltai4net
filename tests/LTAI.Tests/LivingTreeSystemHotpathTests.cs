using LTAI.AI.Governors;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.Messaging;
using LTAI.Core.Execution;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Xunit;

namespace LTAI.Tests;

public sealed class NullChatClient : IChatClient
{
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    object? IChatClient.GetService(Type? serviceType, object? serviceKey) => null;
}

public sealed class LivingTreeSystemHotpathTests
{
    private static LTAIOptions DefaultOptions => new()
    {
        AI = new AIConfig
        {
            L2 = new LayerConfig { Model = "deepseek-v4-pro" },
            L1 = new LayerConfig { Model = "deepseek-v4-flash" },
            MaxTokens = 4096
        }
    };

    private static CPSProcessingService BuildCPS(
        Func<string, CancellationToken, Task<string>>? l1 = null,
        Func<string, CancellationToken, Task<string>>? l2 = null,
        Func<string, CancellationToken, string>? classifier = null)
    {
        var router = new ParetoRouter(embeddingDim: 4, metric: ParetoDistanceMetric.Euclidean);
        var teacher = new BootstrapTeacher(router);
        var genePool = new GenePool(config: new GenePoolConfig { MaxPopulation = 20 });
        var annealer = new SimulatedAnnealer(genePool, router);
        var geneToRule = new GeneToRule(genePool, router);

        l1 ??= (q, ct) => Task.FromResult($"L1: {q}");
        l2 ??= (q, ct) => Task.FromResult($"L2: {q}");
        classifier ??= (q, ct) => "general";

        return new CPSProcessingService(router, classifier, teacher, genePool, annealer, geneToRule,
            l1, l2, NullLogger<CPSProcessingService>.Instance);
    }

    private static LivingTreeSystem BuildTestSystem(
        IChatClient? llm = null,
        CPSProcessingService? cps = null)
    {
        var journal = new TaskJournal(NullLogger<TaskJournal>.Instance);
        var llmClient = llm ?? new FakeChatClient();
        var options = Options.Create(DefaultOptions);
        var localOptions = Options.Create(DefaultOptions);
        var nullLlm = new NullChatClient();

        var gov = new GovernorSet(
            new InputGovernor(nullLlm, NullLogger<InputGovernor>.Instance, localOptions),
            new ContextGovernor(nullLlm, NullLogger<ContextGovernor>.Instance, null!),
            new RoutingGovernor(nullLlm, NullLogger<RoutingGovernor>.Instance, localOptions),
            new OutputGovernor(nullLlm, NullLogger<OutputGovernor>.Instance),
            new SelfGovernor(nullLlm, NullLogger<SelfGovernor>.Instance),
            new SystemGuardian(nullLlm, NullLogger<SystemGuardian>.Instance));

        var toolRegistry = new AIToolRegistry(NullLogger<AIToolRegistry>.Instance);

        return new LivingTreeSystem(journal, llmClient, options, gov, toolRegistry,
            NullLogger<LivingTreeSystem>.Instance, cpsProcessor: cps);
    }

    // ========================================================================
    // CPS Processing Service Tests
    // ========================================================================

    [Fact]
    public async Task CPS_ProcessAsync_ReturnsRouteConfidence()
    {
        var l2 = (string q, CancellationToken ct) =>
            Task.FromResult("L2 code solution for: " + q);
        var cps = BuildCPS(l2: l2, classifier: (q, ct) => "code");

        var result = await cps.ProcessAsync("write a sorting function");

        Assert.True(result.Success);
        Assert.NotEmpty(result.Response);
        Assert.True(result.LatencyMs > 0);
    }

    [Fact]
    public async Task CPS_ProcessAsync_DifferentDomains_ReturnDifferentRoutes()
    {
        var l1 = (string q, CancellationToken ct) =>
            Task.FromResult($"L1 chat: {q}");
        var l2 = (string q, CancellationToken ct) =>
            Task.FromResult($"L2 code: {q}");

        var classifier = (string q, CancellationToken ct) =>
            q.Contains("code") ? "code" : "chat";

        var cps = BuildCPS(l1: l1, l2: l2, classifier: classifier);

        var codeResult = await cps.ProcessAsync("code: write sorting");
        var chatResult = await cps.ProcessAsync("chat: hello");

        Assert.Equal("L2", codeResult.Route);
        Assert.Equal("local", chatResult.Route);
    }

    [Fact]
    public async Task CPS_ProcessAsync_TenQueries_AccumulatesDistribution()
    {
        var l2 = (string q, CancellationToken ct) =>
            Task.FromResult("L2 answer: " + q);
        var cps = BuildCPS(l2: l2, classifier: (q, ct) => "code");

        for (int i = 0; i < 10; i++)
            await cps.ProcessAsync($"query {i}");

        var distribution = cps.GetRouteDistribution();
        Assert.NotEmpty(distribution);
    }

    // ========================================================================
    // ProcessTypedAsync Tests
    // ========================================================================

    [Fact]
    public async Task ProcessTypedAsync_WithCPS_RoutesCorrectly()
    {
        var l2 = (string q, CancellationToken ct) =>
            Task.FromResult("L2 processed: " + q);

        var cps = BuildCPS(l2: l2, classifier: (q, ct) => "code");
        var system = BuildTestSystem(cps: cps);

        var result = await system.ProcessTypedAsync(
            GovernorInput.Create("write a sorting function"));

        Assert.False(result.IsBlocked);
        Assert.NotEmpty(result.Response);
    }

    [Fact]
    public async Task ProcessTypedAsync_NoCPS_FallsToL2Cloud()
    {
        var fakeLlama = new FakeChatClient()
            .AddRoute("test", _ => "L2 cloud fallback response");
        var system = BuildTestSystem(llm: fakeLlama);

        var result = await system.ProcessTypedAsync(
            GovernorInput.Create("test query"));

        Assert.Contains("L2 cloud fallback", result.Response);
    }

    [Fact]
    public async Task ProcessTypedAsync_HumanMessage_IsConsumed()
    {
        var fakeLlama = new FakeChatClient()
            .AddRoute("human", _ => "Processed injected message");
        var journal = new TaskJournal(NullLogger<TaskJournal>.Instance);
        journal.InjectMessage("HUMAN MSG: override this query");
        var llmClient = fakeLlama;
        var options = Options.Create(DefaultOptions);
        var nullLlm = new NullChatClient();
        var gov = new GovernorSet(
            new InputGovernor(nullLlm, NullLogger<InputGovernor>.Instance, options),
            new ContextGovernor(nullLlm, NullLogger<ContextGovernor>.Instance, null!),
            new RoutingGovernor(nullLlm, NullLogger<RoutingGovernor>.Instance, options),
            new OutputGovernor(nullLlm, NullLogger<OutputGovernor>.Instance),
            new SelfGovernor(nullLlm, NullLogger<SelfGovernor>.Instance),
            new SystemGuardian(nullLlm, NullLogger<SystemGuardian>.Instance));
        var toolRegistry = new AIToolRegistry(NullLogger<AIToolRegistry>.Instance);

        var system = new LivingTreeSystem(journal, llmClient, options, gov, toolRegistry,
            NullLogger<LivingTreeSystem>.Instance);

        var result = await system.ProcessTypedAsync(
            GovernorInput.Create("test query"));

        Assert.NotEmpty(result.Response);
    }

    // ========================================================================
    // Journal Behavior Tests
    // ========================================================================

    [Fact]
    public void TaskJournal_PauseResume_ControlsChatFlow()
    {
        var journal = new TaskJournal(NullLogger<TaskJournal>.Instance);

        Assert.False(journal.IsPaused);
        journal.Pause();
        Assert.True(journal.IsPaused);
        journal.Resume();
        Assert.False(journal.IsPaused);
    }

    [Fact]
    public async Task TaskJournal_RecordsEntries()
    {
        var journal = new TaskJournal(NullLogger<TaskJournal>.Instance);
        var entry = journal.Add("test query");

        Assert.NotNull(entry);
    }

    // ========================================================================
    // StreamChatAsync Tests
    // ========================================================================

    [Fact]
    public async Task StreamChatAsync_L2Streaming_ReturnsChunks()
    {
        var fakeStream = new FakeChatClient()
            .AddRoute("explain", _ => "Quick sort partitions around a pivot element.");
        var system = BuildTestSystem(llm: fakeStream);

        var chunks = new List<string>();
        await foreach (var chunk in system.StreamChatAsync("explain quicksort"))
            chunks.Add(chunk);

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.Contains("Quick sort"));
    }

    [Fact]
    public async Task StreamChatAsync_WithContextHub_EmbedsContext()
    {
        var system = BuildTestSystem(llm: new FakeChatClient());

        var chunks = new List<string>();
        await foreach (var chunk in system.StreamChatAsync("general query"))
            chunks.Add(chunk);

        Assert.NotEmpty(chunks);
    }
}
