using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Context;

public sealed class CCRProvider : AIContextProvider
{
    private readonly CompressionStore _store;
    private readonly ILogger<CCRProvider> _logger;
    private readonly int _thresholdTokens;

    public CCRProvider(CompressionStore store, ILogger<CCRProvider> logger,
        int thresholdTokens = 200)
        : base(null, null, null)
    {
        _store = store;
        _logger = logger;
        _thresholdTokens = thresholdTokens;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var messages = context.AIContext.Messages;
        if (messages == null || !messages.Any())
            return context.AIContext;

        var newMessages = new List<ChatMessage>(messages.Count());
        var anyCompressed = false;

        foreach (var msg in messages)
        {
            if (msg.Text == null || msg.Text.Length < 50)
            {
                newMessages.Add(msg);
                continue;
            }

            var estTokens = CompressionStore.EstimateTokens(msg.Text);
            if (estTokens < _thresholdTokens)
            {
                newMessages.Add(msg);
                continue;
            }

            var (compressed, summary) = ContentCompressor.CompressWithSummary(msg.Text);
            var type = ContentCompressor.Detect(msg.Text);
            var id = _store.Store(msg.Text, summary, type);

            var marker = $""""
[CCR: id="{id}", original={estTokens}t, type={type.ToString().ToLowerInvariant()}, summary: {summary}]
{compressed}

> ℹ 如需查看未压缩的原始内容，请调用 `retrieve_content(id: "{id}")`
"""";

            newMessages.Add(new ChatMessage(msg.Role, marker)
            {
                RawRepresentation = msg.RawRepresentation,
                AdditionalProperties = msg.AdditionalProperties
            });

            anyCompressed = true;
        }

        if (anyCompressed)
        {
            _logger.LogDebug("CCR: compressed messages to store");
        }

        return new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages = newMessages.AsReadOnly()
        };
    }
}
