using LTAI.AI.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Providers;

public sealed class RescueParsingChatClient : DelegatingChatClient
{
    private readonly ILogger<RescueParsingChatClient>? _logger;

    public RescueParsingChatClient(IChatClient innerClient, ILogger<RescueParsingChatClient>? logger = null)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct);
        return RescueResponse(response);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, ct))
            yield return update;
    }

    private ChatResponse RescueResponse(ChatResponse response)
    {
        var rescued = false;
        foreach (var msg in response.Messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.Arguments?.Count > 0)
                {
                    var argsJson = System.Text.Json.JsonSerializer.Serialize(fcc.Arguments);
                    var parsed = RescueParser.TryParseToolCall(argsJson);
                    if (parsed != null)
                    {
                        _logger?.LogInformation("Rescue parser fixed tool call args for {Tool}", fcc.Name);
                        rescued = true;
                    }
                }
            }
        }

        if (rescued)
            _logger?.LogInformation("Rescue parser applied to response");

        return response;
    }
}
