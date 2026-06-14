using System.Text.Json;
using LTAI.Core.Configuration;
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
    private readonly int _maxToolTokens;

    /// <summary>
    /// Token-aware message reducer. Caps at maxCount messages, but also enforces
    /// a max token budget for tool messages to prevent large tool outputs from
    /// consuming the entire context window.
    /// </summary>
    /// <param name="maxCount">Hard cap on total message count (default 200).</param>
    /// <param name="maxToolTokens">Max estimated tokens for preserved tool messages (default 8000).</param>
    public MaxMessageCountReducer(int maxCount, int maxToolTokens = 8000)
    {
        _maxCount = Math.Max(10, maxCount);
        _maxToolTokens = Math.Max(1000, maxToolTokens);
    }

    public MaxMessageCountReducer(int maxCount) : this(maxCount, 8000) { }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Count <= _maxCount)
            return Task.FromResult<IEnumerable<ChatMessage>>(ReduceToolTokens(list));

        // Single pass: classify messages and build result
        var toolSet = new HashSet<int>(); // indices of tool messages
        var result = new List<ChatMessage>();
        var recentBuf = new List<ChatMessage>();
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            if (m.Role == ChatRole.System)
            {
                result.Add(m);
                continue;
            }
            if (IsToolMessage(m))
            {
                toolSet.Add(i);
                result.Add(m);
                continue;
            }
            recentBuf.Add(m);
        }

        var budget = _maxCount - result.Count;
        if (budget > 0 && recentBuf.Count > 0)
            result.AddRange(recentBuf.TakeLast(Math.Min(budget, recentBuf.Count)));

        return Task.FromResult<IEnumerable<ChatMessage>>(ReduceToolTokens(result));
    }

    private static bool IsToolMessage(ChatMessage m)
    {
        var contents = m.Contents;
        if (contents == null) return false;
        foreach (var c in contents)
        {
            if (c is FunctionCallContent || c is FunctionResultContent || c is ToolApprovalRequestContent)
                return true;
        }
        return false;
    }

    /// <summary>Trim oldest tool messages if total estimated tokens exceed budget.</summary>
    private IEnumerable<ChatMessage> ReduceToolTokens(List<ChatMessage> messages)
    {
        var toolEntries = new List<(int Index, int Tokens)>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (IsToolMessage(messages[i]))
                toolEntries.Add((i, TokenEstimator.Estimate(messages[i].Text ?? "")));
        }

        var totalToolTokens = toolEntries.Sum(t => t.Tokens);
        if (totalToolTokens <= _maxToolTokens || toolEntries.Count == 0)
            return messages;

        // Trim oldest tool messages first until under budget
        var toRemove = 0;
        var running = totalToolTokens;
        foreach (var (_, tokens) in toolEntries)
        {
            running -= tokens;
            toRemove++;
            if (running <= _maxToolTokens) break;
        }
        if (toRemove == 0 || toRemove >= toolEntries.Count)
            return messages;

        // Build result: skip first N tool message indices
        var trimmedIndices = new HashSet<int>(toolEntries.Take(toRemove).Select(t => t.Index));
        var result = new List<ChatMessage>(messages.Count - toRemove);
        var noteInserted = false;
        for (int i = 0; i < messages.Count; i++)
        {
            if (trimmedIndices.Contains(i)) continue;
            if (!noteInserted && toRemove > 0 && messages[i].Role == ChatRole.System)
            {
                result.Add(messages[i]);
                result.Add(new ChatMessage(ChatRole.System,
                    $"[{toRemove} tool messages trimmed to stay within {_maxToolTokens} token budget]"));
                noteInserted = true;
            }
            else
            {
                result.Add(messages[i]);
            }
        }

        return result;
    }
}
