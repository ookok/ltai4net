using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Desktop.Services;

public sealed class LlmClient : ILlmClient
{
    private readonly IChatService _svc;

    public LlmClient(IChatService svc)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        return await _svc.ChatAsync(message, ct: ct) ?? "";
    }

    public async IAsyncEnumerable<string> ChatStreamingAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _svc.ChatStreamingAsync(message, ct: ct))
        {
            if (update.Text is { Length: > 0 } text)
                yield return text;
        }
    }
}
