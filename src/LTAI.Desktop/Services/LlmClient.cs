using LTAI.Agent;
using Microsoft.Extensions.AI;

namespace LTAI.Desktop.Services;

public sealed class LlmClient : ILlmClient
{
    private readonly ChatAgent _agent;

    public LlmClient(ChatAgent agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        return await _agent.ChatAsync(message, ct: ct) ?? "";
    }

    public async IAsyncEnumerable<string> ChatStreamingAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _agent.ChatStreamingAsync(message, ct))
        {
            if (update.Text is { Length: > 0 } text)
                yield return text;
        }
    }
}
