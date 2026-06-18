using System.Runtime.CompilerServices;
using LTAI.Core.Session;

namespace LTAI.Desktop;

/// <summary>Streaming chat service abstraction. Enables mocking ChatAgent in tests.</summary>
public interface IChatService
{
    IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> ChatStreamingAsync(
        string message, ISessionHandle? sessionHandle = null,
        CancellationToken ct = default);
    Task<string> ChatAsync(string message, ISessionHandle? sessionHandle = null, CancellationToken ct = default);
}

/// <summary>Wraps ChatAgent as IChatService for DI registration.</summary>
public sealed class ChatServiceProxy : IChatService
{
    private readonly LTAI.Agent.ChatAgent _inner;
    public ChatServiceProxy(LTAI.Agent.ChatAgent inner) => _inner = inner;

    public IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> ChatStreamingAsync(
        string message, ISessionHandle? sessionHandle = null, CancellationToken ct = default)
        => _inner.ChatStreamingAsync(message, sessionHandle, ct);

    public Task<string> ChatAsync(string message, ISessionHandle? sessionHandle = null, CancellationToken ct = default)
        => _inner.ChatAsync(message, sessionHandle, userId: null, ct);
}
