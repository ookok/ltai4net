using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.MAF;

public sealed class LTAIAgent : AIAgent
{
    private readonly ChatClientAgent _chatAgent;
    private readonly ILogger<LTAIAgent> _logger;

    public override string? Name => "LTAI";
    public override string? Description => "LivingTree AI Agent";

    public LTAIAgent(LivingTreeChatClient chatClient, ILogger<LTAIAgent> logger)
    {
        _logger = logger;
        _chatAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "LTAI", Description = "LivingTree AI Agent"
        });
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken ct = default)
        => await _chatAgent.RunAsync(messages, session, options, ct).ConfigureAwait(false);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var u in _chatAgent.RunStreamingAsync(messages, session, options, ct))
            yield return u;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct)
        => ValueTask.FromResult<AgentSession>(new LTAIAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options, CancellationToken ct)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session, options));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement data, JsonSerializerOptions? options, CancellationToken ct)
        => ValueTask.FromResult<AgentSession>(JsonSerializer.Deserialize<LTAIAgentSession>(data.GetRawText(), options) ?? new LTAIAgentSession());
}

internal sealed class LTAIAgentSession : AgentSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public List<ChatMessage> History { get; set; } = new();
    public int TurnCount { get; set; }
}

public sealed class LivingTreeChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ILogger<LivingTreeChatClient>? _logger;

    public LivingTreeChatClient(IChatClient inner, ILogger<LivingTreeChatClient>? logger = null)
    {
        _inner = inner;
        _logger = logger;
    }

    public ChatClientMetadata? Metadata =>
        new("LTAI", new Uri("https://github.com/ltai-org/ltai4net"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        _logger?.LogDebug("LivingTreeChatClient forwarding {Count} messages", messages.Count());
        return await _inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _inner.GetStreamingResponseAsync(messages, options, ct))
            yield return chunk;
    }

    object? IChatClient.GetService(Type? t, object? k) => _inner.GetService(t ?? typeof(IChatClient), k);
    void IDisposable.Dispose() { }
}
