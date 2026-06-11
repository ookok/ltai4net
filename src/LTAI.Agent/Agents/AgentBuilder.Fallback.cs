using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

internal sealed class FallbackAgent : AIAgent
{
    public FallbackAgent(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public override string? Name { get; }
    public override string? Description { get; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(JsonSerializer.SerializeToElement(new { fallback = true }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]")));

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => AsyncEnumerable.Repeat(new AgentResponseUpdate(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]"), 1);
}

file sealed class MinimalAgentSession : AgentSession
{
    public MinimalAgentSession() : base(new AgentSessionStateBag()) { }
}

internal sealed class MaxMessageCountReducer : IChatReducer
{
    private readonly int _maxCount;
    public MaxMessageCountReducer(int maxCount) => _maxCount = Math.Max(10, maxCount);

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Count <= _maxCount)
            return Task.FromResult<IEnumerable<ChatMessage>>(list);

        var systemMessages = list.Where(m => m.Role == ChatRole.System).ToList();
        var toolMessages = list.Where(m => m.Contents?.Any(c =>
            c is Microsoft.Extensions.AI.FunctionCallContent ||
            c is Microsoft.Extensions.AI.FunctionResultContent ||
            c is Microsoft.Extensions.AI.ToolApprovalRequestContent) == true).ToList();

        var nonSystemNonTool = list.Where(m => m.Role != ChatRole.System &&
            !toolMessages.Contains(m)).ToList();
        var budget = _maxCount - systemMessages.Count - toolMessages.Count;
        var recent = nonSystemNonTool.TakeLast(Math.Max(0, budget)).ToList();

        var result = new List<ChatMessage>();
        result.AddRange(systemMessages);
        result.AddRange(toolMessages);
        result.AddRange(recent);

        return Task.FromResult<IEnumerable<ChatMessage>>(result);
    }
}
