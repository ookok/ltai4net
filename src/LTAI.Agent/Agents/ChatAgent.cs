using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ChatAgent : AIAgent
{
    private readonly ChatClientAgent _inner;
    private readonly ILogger<ChatAgent> _logger;
    private readonly List<(string role, string content)> _conversationHistory = new();
    private const int MaxHistoryTurns = 20;

    public override string Name { get; }
    public override string Description { get; }

    public ChatAgent(
        IChatClient chatClient,
        LTAIAgentCard card,
        IEnumerable<Microsoft.Extensions.AI.AITool> tools,
        ILogger<ChatAgent> logger)
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        _inner = chatClient.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions,
            ChatOptions = new() { Tools = tools.ToList() }
        });
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No user message received."));

        var query = userMsg.Text ?? "";
        _logger.LogInformation("ChatAgent [{Name}]: {Query}", Name, query[..Math.Min(query.Length, 200)]);

        UpdateHistory("user", query);

        if (query.Trim().StartsWith('/'))
        {
            return await HandleSlashCommand(query, session, options, cancellationToken);
        }

        if (_conversationHistory.Count > 6)
        {
            var systemMsg = new ChatMessage(ChatRole.System,
                "Previous conversation summary:\n" + string.Join("\n",
                    _conversationHistory.TakeLast(10).Select(h => $"[{h.role}]: {h.content[..Math.Min(h.content.Length, 200)]}")));
            msgList.Insert(0, systemMsg);
        }

        var response = await _inner.RunAsync(messages, session, options, cancellationToken);

        var responseText = response.Text ?? "";
        UpdateHistory("assistant", responseText[..Math.Min(responseText.Length, 500)]);

        return response;
    }

    private async Task<AgentResponse> HandleSlashCommand(
        string command, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
    {
        var cmd = command.Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "/help":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    "LTAI Chat Agent. Commands: /help /status /clear /budget"));
            case "/status":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    $"Chat Agent [{Name}] — History: {_conversationHistory.Count} turns, Model: deepseek-v4-pro"));
            case "/clear":
                _conversationHistory.Clear();
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "Conversation history cleared."));
            case "/budget":
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "Budget tracking is enabled for this agent."));
            default:
                return await _inner.RunAsync(
                    [new ChatMessage(ChatRole.User, command)],
                    session, options, ct);
        }
    }

    private void UpdateHistory(string role, string content)
    {
        _conversationHistory.Add((role, content));
        while (_conversationHistory.Count > MaxHistoryTurns)
            _conversationHistory.RemoveAt(0);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner.RunStreamingAsync(messages, session, options, cancellationToken))
            yield return update;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(cancellationToken);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.SerializeSessionAsync(session, o, ct);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.DeserializeSessionAsync(state, o, ct);
}
