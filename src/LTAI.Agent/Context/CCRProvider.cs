using LTAI.Agent.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Context;

public sealed class CCRProvider : AIContextProvider
{
    private readonly CompressionStore _store;
    private readonly ILogger<CCRProvider> _logger;
    private readonly int _baseThresholdTokens;
    private int _adaptiveThresholdTokens;

    // Adaptive threshold configuration
    private const int MinThreshold = 100;
    private const int MaxThreshold = 1000;
    private const double LowUsageRatio = 0.3;   // Below this, increase threshold (compress less)
    private const double HighUsageRatio = 0.7;  // Above this, decrease threshold (compress more)

    public CCRProvider(CompressionStore store, ILogger<CCRProvider> logger,
        int thresholdTokens = 200)
        : base(null, null, null)
    {
        _store = store;
        _logger = logger;
        _baseThresholdTokens = thresholdTokens;
        _adaptiveThresholdTokens = thresholdTokens;
    }

    /// <summary>
    /// Get current adaptive threshold based on context usage.
    /// When context is underutilized, we compress less aggressively.
    /// When context is nearly full, we compress more aggressively.
    /// </summary>
    private int GetAdaptiveThreshold()
    {
        var usageRatio = MemoryBudget.GetMemoryUsageRatio();

        if (usageRatio < LowUsageRatio)
        {
            // Under 30% usage: relax compression (higher threshold)
            _adaptiveThresholdTokens = Math.Min(MaxThreshold,
                (int)(_baseThresholdTokens * (1 + (LowUsageRatio - usageRatio) * 2)));
        }
        else if (usageRatio > HighUsageRatio)
        {
            // Over 70% usage: tighten compression (lower threshold)
            _adaptiveThresholdTokens = Math.Max(MinThreshold,
                (int)(_baseThresholdTokens * (1 - (usageRatio - HighUsageRatio) * 2)));
        }
        else
        {
            // Normal range: use base threshold
            _adaptiveThresholdTokens = _baseThresholdTokens;
        }

        return _adaptiveThresholdTokens;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var messages = context.AIContext.Messages;
        if (messages == null || !messages.Any())
            return context.AIContext;

        var threshold = GetAdaptiveThreshold();
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
            if (estTokens < threshold)
            {
                newMessages.Add(msg);
                continue;
            }

            var (compressed, summary) = ContentCompressor.CompressWithSummary(msg.Text);
            var type = ContentCompressor.Detect(msg.Text);
            var id = _store.Store(msg.Text, summary, type);

            var marker = string.Format(
                "[CCR: id=\"{0}\", original={1}t, type={2}, summary: {3}]\n{4}\n\n> \u2139 \u5982\u9700\u67E5\u770B\u672A\u538B\u7F29\u7684\u539F\u59CB\u5185\u5BB9\uFF0C\u8BF7\u8C03\u7528 `retrieve_content(id: \"{0}\")`",
                id, estTokens, type.ToString().ToLowerInvariant(), summary, compressed);

            newMessages.Add(new ChatMessage(msg.Role, marker)
            {
                RawRepresentation = msg.RawRepresentation,
                AdditionalProperties = msg.AdditionalProperties
            });

            anyCompressed = true;
        }

        if (anyCompressed)
        {
            _logger.LogDebug("CCR: compressed messages (threshold={Threshold}, usage={Usage:P0})",
                threshold, MemoryBudget.GetMemoryUsageRatio());
        }

        return new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages = newMessages.AsReadOnly()
        };
    }
}
