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
/// Threshold: when UsageTracker.ContextRatio() > RatioThreshold (default 0.75).
/// </summary>
public sealed class CompactionStep : IPipelineStep
{
    private readonly ILogger<CompactionStep> _logger;
    private readonly double _ratioThreshold;

    public string Name => "Compaction";

    /// <summary>
    /// Compression ratio threshold. When ContextRatio exceeds this,
    /// compression is triggered. Default 0.75 (75% context usage).
    /// </summary>
    public double RatioThreshold => _ratioThreshold;

    public CompactionStep(
        ILogger<CompactionStep>? logger = null,
        double ratioThreshold = 0.75)
    {
        _logger = logger ?? NullLogger<CompactionStep>.Instance;
        _ratioThreshold = ratioThreshold;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // Check context ratio
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

        // Compress each message text content
        for (int i = 0; i < context.Messages.Count; i++)
        {
            var msg = context.Messages[i];
            if (string.IsNullOrEmpty(msg.Text)) continue;

            originalLength += msg.Text.Length;

            // Compress based on content type
            var contentType = ContentCompressor.Detect(msg.Text);
            var compressed = ContentCompressor.Compress(msg.Text, contentType);

            if (compressed.Length < msg.Text.Length)
            {
                // Replace text with compressed version
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
                "CompactionStep: compressed {Count}/{Total} messages ({Ratio:P0} of original size)",
                compressedCount, context.Messages.Count, ratio);
        }
        else
        {
            _logger.LogDebug("CompactionStep: no messages were compressed");
        }

        return context;
    }
}
