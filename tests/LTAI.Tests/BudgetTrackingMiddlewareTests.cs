using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Agent.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class BudgetTrackingMiddlewareTests
{
    private readonly ILogger<BudgetTrackingMiddleware> _logger = NullLogger<BudgetTrackingMiddleware>.Instance;

    private static IEnumerable<ChatMessage> ShortMessage()
    {
        return new[] { new ChatMessage(ChatRole.User, "Hello") };
    }

    private static IEnumerable<ChatMessage> LongMessage()
    {
        var text = new string('x', 10_000);
        return new[] { new ChatMessage(ChatRole.User, text) };
    }

    [Fact]
    public async Task SmallRequest_PassesBudgetCheck()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 100_000);

        var agent = new TestAgent();
        var response = await middleware.InvokeAsync(ShortMessage(), null, null, agent, CancellationToken.None);

        Assert.Contains("Hello from TestAgent", response.Text);
    }

    [Fact]
    public async Task TracksTokenUsage_AcrossRequests()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 100_000);

        var agent = new TestAgent();
        await middleware.InvokeAsync(LongMessage(), null, null, agent, CancellationToken.None);

        var budget = middleware.GetBudget("TestAgent");
        Assert.True(budget.TotalTokens > 0);
        Assert.True(budget.RequestCount == 1);
    }

    [Fact]
    public async Task ExceededTokenLimit_BlocksRequest()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 100);

        var agent = new TestAgent();
        var response = await middleware.InvokeAsync(LongMessage(), null, null, agent, CancellationToken.None);

        Assert.Contains("[Budget]", response.Text);
        Assert.Contains("token limit", response.Text);
    }

    [Fact]
    public async Task ExceededCostLimit_BlocksRequest()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 1_000_000, dailyCostLimitUsd: 0.0001m);

        var agent = new TestAgent();
        var response = await middleware.InvokeAsync(LongMessage(), null, null, agent, CancellationToken.None);

        Assert.Contains("[Budget]", response.Text);
        Assert.Contains("cost limit", response.Text);
    }

    [Fact]
    public async Task SeparateAgents_HaveSeparateBudgets()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 50_000);

        var agent1 = new TestAgent("Agent1");
        var agent2 = new TestAgent("Agent2");

        await middleware.InvokeAsync(LongMessage(), null, null, agent1, CancellationToken.None);
        await middleware.InvokeAsync(LongMessage(), null, null, agent2, CancellationToken.None);

        var b1 = middleware.GetBudget("Agent1");
        var b2 = middleware.GetBudget("Agent2");

        Assert.True(b1.TotalTokens > 0);
        Assert.True(b2.TotalTokens > 0);
    }

    [Fact]
    public async Task GetAllBudgets_ReturnsAllTrackedAgents()
    {
        var middleware = new BudgetTrackingMiddleware(_logger);

        var agent = new TestAgent("BudgetAgent");
        await middleware.InvokeAsync(ShortMessage(), null, null, agent, CancellationToken.None);

        var all = middleware.GetAllBudgets();
        Assert.Contains("BudgetAgent", all.Keys);
    }

    [Fact]
    public async Task UnknownAgent_ReturnsEmptyBudget()
    {
        var middleware = new BudgetTrackingMiddleware(_logger);
        var budget = middleware.GetBudget("NonExistentAgent");

        Assert.Equal(0, budget.TotalTokens);
        Assert.Equal(0m, budget.TotalCost);
    }

    [Fact]
    public async Task TracksCost_BasedOnModelPricing()
    {
        var middleware = new BudgetTrackingMiddleware(_logger, dailyTokenLimit: 100_000, dailyCostLimitUsd: 100m);

        var agent = new TestAgent();
        await middleware.InvokeAsync(LongMessage(), null, null, agent, CancellationToken.None);

        var budget = middleware.GetBudget("TestAgent");
        Assert.True(budget.TotalCost > 0m);
        Assert.True(budget.RequestCount == 1);
    }

    private sealed class TestAgent : AIAgent
    {
        private readonly string _name;

        public TestAgent(string name = "TestAgent")
        {
            _name = name;
        }

        public override string? Name => _name;
        public override string? Description => "Test agent for budget tracking";

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "Hello from TestAgent")));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "Hello from TestAgent");
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TestAgentSession());

        private sealed class TestAgentSession : AgentSession { }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TestAgentSession());
    }
}
