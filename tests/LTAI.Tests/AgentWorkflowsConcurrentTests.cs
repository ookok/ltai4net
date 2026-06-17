using LTAI.Agent.Workflows;
using LTAI.Agent.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Text.Json;

namespace LTAI.Tests;

public sealed class AgentWorkflowsConcurrentTests
{
    private readonly AgentWorkflows _workflows;

    public AgentWorkflowsConcurrentTests()
    {
        var agents = new AIAgent[] { new TestAgent("test-a"), new TestAgent("test-b"), new TestAgent("test-c") };
        var router = new TestAgent("router");
        _workflows = new AgentWorkflows(agents, router,
            NullLogger<AgentWorkflows>.Instance,
            workflowTimeout: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NGT_RunsBlindThenDiscuss()
    {
        var result = await _workflows.RunConcurrentAsync(
            ["test-a", "test-b"],
            "solve the problem",
            ConcurrentMode.NGT,
            ct: CancellationToken.None);

        Assert.Contains("test-a", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-b", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NGT_WithSingleAgent_ReturnsSingleResult()
    {
        var result = await _workflows.RunConcurrentAsync(
            ["test-a"],
            "single task",
            ConcurrentMode.NGT,
            ct: CancellationToken.None);

        Assert.Contains("test-a", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subgroup_PartitionsAndAggregates()
    {
        var result = await _workflows.RunConcurrentAsync(
            ["test-a", "test-b", "test-c"],
            "collaborate on design",
            ConcurrentMode.Subgroup,
            ct: CancellationToken.None);

        Assert.Contains("test-a", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-b", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-c", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subgroup_WithSingleAgent_ReturnsSingleResult()
    {
        var result = await _workflows.RunConcurrentAsync(
            ["test-a"],
            "single task",
            ConcurrentMode.Subgroup,
            ct: CancellationToken.None);

        Assert.Contains("test-a", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllModes_WithEmptyAgentList_ReturnsNoValid()
    {
        foreach (ConcurrentMode mode in Enum.GetValues<ConcurrentMode>())
        {
            var result = await _workflows.RunConcurrentAsync(
                [],
                "anything",
                mode,
                ct: CancellationToken.None);

            Assert.Contains("No valid agents", result, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A minimal MAF agent that echoes its name and the task.</summary>
    private sealed class TestAgent : AIAgent
    {
        private readonly string _name;
        public override string? Name => _name;
        protected override string? IdCore => _name + "-id";

        public TestAgent(string name) => _name = name;

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken ct = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            return Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, $"echo({_name}): {last}")]
            });
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var last = "";
            foreach (var m in messages) last = m.Text ?? last;
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"echo({_name}): {last}");
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default)
            => throw new NotSupportedException("Test agent is a test stub.");
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException("Test agent is a test stub.");
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement state,
            JsonSerializerOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException("Test agent is a test stub.");
    }
}
