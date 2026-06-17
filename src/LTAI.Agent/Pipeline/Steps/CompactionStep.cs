// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CompactionStep — history/token compression
//
//  Phase 3b: wraps ContentCompressor / TrimHistory logic.
//  When context usage exceeds a threshold, the step compresses
//  accumulated messages to fit within budget.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Context;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that compresses accumulated messages when context usage
/// exceeds a configurable threshold. Uses ContentCompressor for message
/// compression and tracks the compression ratio for telemetry.
///
/// Uses <see cref="TieredCompressor"/> for priority-aware compression:
/// older/low-priority messages are compressed more aggressively,
/// recent/critical messages retain more detail.
///
/// Threshold: when UsageTracker.ContextRatio() > RatioThreshold (default 0.75).
/// </summary>
public sealed class CompactionStep : IPipelineStep
{
    private readonly ILogger<CompactionStep> _logger;
    private readonly double _ratioThreshold;
    private readonly TieredCompressor _tiered;

    public string Name => "Compaction";

    /// <summary>
    /// Compression ratio threshold. When ContextRatio exceeds this,
    /// compression is triggered. Default 0.75 (75% context usage).
    /// </summary>
    public double RatioThreshold => _ratioThreshold;

    public CompactionStep(
        ILogger<CompactionStep>? logger = null,
        double ratioThreshold = 0.75,
        TieredCompressor? tieredCompressor = null)
    {
        _logger = logger ?? NullLogger<CompactionStep>.Instance;
        _ratioThreshold = ratioThreshold;
        _tiered = tieredCompressor ?? new TieredCompressor();
    }

    // Parameterless ctor for direct instantiation
    public CompactionStep() : this(null, 0.75, null) { }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var contextRatio = UsageTracker.ContextRatio();
        if (contextRatio < _ratioThreshold)
        {
            _logger.LogDebug("CompactionStep: context {Pct:F0}% < threshold {Threshold:P0}, skipping",
                contextRatio * 100, _ratioThreshold);
            return context;
        }

        _logger.LogInformation("CompactionStep: context {Pct:F0}% > threshold {Threshold:P0}, compressing",
            contextRatio * 100, _ratioThreshold);

        var compressedCount = 0;
        var originalLength = 0;
        var compressedLength = 0;
        var lowPriorityCount = 0;

        for (int i = 0; i < context.Messages.Count; i++)
        {
            var msg = context.Messages[i];
            if (string.IsNullOrEmpty(msg.Text)) continue;

            var tier = _tiered.Classify(i, context.Messages.Count);
            var ratio = _tiered.GetCompressionRatio(tier);
            originalLength += msg.Text.Length;

            if (tier == Context.CompressTier.LowPriority) lowPriorityCount++;

            var compressed = CompressWithRatio(msg.Text, ratio);

            if (compressed.Length < msg.Text.Length)
            {
                context.Messages[i] = new Microsoft.Extensions.AI.ChatMessage(msg.Role, compressed)
                {
                    AuthorName = msg.AuthorName,
                    RawRepresentation = msg.RawRepresentation,
                    AdditionalProperties = msg.AdditionalProperties,
                };
                compressedLength += compressed.Length;
                compressedCount++;
            }
            else
            {
                compressedLength += msg.Text.Length;
            }
        }

        if (compressedCount > 0)
        {
            var ratio = originalLength > 0
                ? (double)compressedLength / originalLength
                : 1.0;
            _logger.LogInformation(
                "CompactionStep: compressed {Count}/{Total} messages ({Ratio:P0}) | {LowPrio} low-priority",
                compressedCount, context.Messages.Count, ratio, lowPriorityCount);

            var summary = _tiered.SummarizeTierStats(context.Messages.Count, compressedCount, lowPriorityCount);
            context.Set("CompactionSummary", summary);
        }
        else
        {
            _logger.LogDebug("CompactionStep: no messages were compressed");
        }

        return context;
    }

    private static string CompressWithRatio(string text, double ratio)
    {
        if (ratio >= 1.0) return text;
        var contentType = ContentCompressor.Detect(text);
        var compressed = ContentCompressor.Compress(text, contentType);
        if (ratio >= 0.7 || compressed.Length >= text.Length * ratio)
            return compressed;
        var targetLen = (int)(text.Length * ratio);
        if (targetLen < 50) targetLen = 50;
        return text.Length <= targetLen ? text : text[..targetLen] + "\n...(压缩)";
    }
}
