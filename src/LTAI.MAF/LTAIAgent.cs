using LTAI.AI.Governors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.MAF;

public sealed class LTAIAgent : IChatClient
{
    private readonly LivingTreeSystem _livingTree;
    private readonly ILogger<LTAIAgent> _logger;
    private readonly LTAIInputFilter? _inputFilter;
    private readonly LTAIOutputFilter? _outputFilter;

    public string Name => "LTAI";
    public string Description => "LivingTree AI Agent with bio-inspired governance";

    public ChatClientMetadata? Metadata => new("LTAI");

    public LTAIAgent(
        LivingTreeSystem livingTree,
        ILogger<LTAIAgent> logger,
        LTAIInputFilter? inputFilter = null,
        LTAIOutputFilter? outputFilter = null)
    {
        _livingTree = livingTree;
        _logger = logger;
        _inputFilter = inputFilter;
        _outputFilter = outputFilter;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();

        if (userMessages.Count == 0)
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "No user message received."));

        var query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));

        _inputFilter?.Analyze(userMessages.Last());

        _logger.LogInformation("LTAI agent: {Query}", query[..Math.Min(query.Length, 200)]);

        try
        {
            var result = await _livingTree.ChatAsync(query, cancellationToken);

            if (_outputFilter != null)
                result = _outputFilter.Review(result);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LTAI agent error");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Error: {ex.Message}"));
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var text = response.Text ?? "";

        const int chunkSize = 8;
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = text[i..Math.Min(i + chunkSize, text.Length)];
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
            return Metadata;

        return null;
    }

    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
