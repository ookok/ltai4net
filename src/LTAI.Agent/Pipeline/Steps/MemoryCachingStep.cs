// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MemoryCachingStep — pipeline step for state checkpointing
//
//  Inspiration: Memory Caching (arXiv 2602.24281)
//
//  Three functions in the pipeline:
//    1. Save checkpoint after RouterStep (at token-count milestones)
//    2. Restore checkpoint before RouterStep (find nearest state)
//    3. Automatically checkpoint every N tokens (configurable)
//
//  Placement in pipeline:
//    LoraAdapterStep → MemoryCachingStep(restore) → RagContextStep
//    → RouterStep → ToolExecutionStep → CompactionStep
//    → MemoryCachingStep(save) [added via AfterRouter flag]
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that saves and restores conversation state checkpoints.
/// Works with the CachingCascade (Memory → Sqlite → Null) to provide
/// efficient state restoration in long-running conversations.
///
/// Checkpoint strategy:
///   - Save every CheckpointInterval tokens (default 512)
///   - Restore nearest checkpoint before RouterStep
///   - Use session ID from context for key scoping
/// </summary>
public sealed class MemoryCachingStep : IPipelineStep
{
    private readonly Caching.IMemoryCachingStore _store;
    private readonly ILogger<MemoryCachingStep> _logger;
    private readonly long _checkpointInterval;
    private readonly bool _isAfterRouter;

    public string Name => _isAfterRouter ? "MemoryCaching(Save)" : "MemoryCaching(Restore)";

    /// <summary>
    /// Tokens between automatic checkpoints. Default 512.
    /// </summary>
    public long CheckpointInterval => _checkpointInterval;

    /// <summary>
    /// Create a MemoryCachingStep.
    /// </summary>
    /// <param name="store">The caching cascade.</param>
    /// <param name="afterRouter">
    ///   false = before RouterStep (restore mode, default)
    ///   true  = after RouterStep (save mode, saves to Sqlite)
    /// </param>
    /// <param name="checkpointInterval">Token count between checkpoints.</param>
    /// <param name="logger">Optional logger.</param>
    public MemoryCachingStep(
        Caching.IMemoryCachingStore store,
        bool afterRouter = false,
        long checkpointInterval = 512,
        ILogger<MemoryCachingStep>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _isAfterRouter = afterRouter;
        _checkpointInterval = checkpointInterval > 0 ? checkpointInterval : 512;
        _logger = logger ?? NullLogger<MemoryCachingStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // Get session ID from context
        var sessionId = context.TraceId ?? "default";

        if (_isAfterRouter)
        {
            // SAVE mode — after RouterStep
            await SaveCheckpointAsync(context, sessionId).ConfigureAwait(false);
        }
        else
        {
            // RESTORE mode — before RouterStep
            await RestoreCheckpointAsync(context, sessionId).ConfigureAwait(false);
        }

        return context;
    }

    private async Task SaveCheckpointAsync(MessageContext context, string sessionId)
    {
        if (context.Result == null) return;

        // Estimate token count from messages
        var tokenCount = EstimateTokenCount(context);

        // Check if we should checkpoint now
        var lastCheckpoint = context.TryGet<long>("_lastCheckpointToken", out var lastToken) ? lastToken : 0L;
        if (tokenCount - lastCheckpoint < _checkpointInterval)
            return;

        // Build checkpoint key
        var key = $"session:{sessionId}:pos:{tokenCount}";

        // Serialize checkpoint data (compact json summary)
        var checkpointData = BuildCheckpointData(context, sessionId, tokenCount);

        try
        {
            await _store.StoreAsync(key, checkpointData, tokenCount, context.CancellationToken)
                .ConfigureAwait(false);

            context.Set("_lastCheckpointToken", tokenCount);
            _logger.LogDebug(
                "MemoryCaching: saved checkpoint '{Key}' at {Tokens} tokens",
                key, tokenCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryCaching: save failed for key '{Key}'", key);
        }
    }

    private async Task RestoreCheckpointAsync(MessageContext context, string sessionId)
    {
        var currentTokens = EstimateTokenCount(context);
        if (currentTokens < _checkpointInterval)
            return; // Not enough history to restore from

        // Find nearest checkpoint
        try
        {
            var nearest = await _store.FindNearestAsync(sessionId, currentTokens, context.CancellationToken)
                .ConfigureAwait(false);

            if (nearest == null)
            {
                _logger.LogDebug("MemoryCaching: no checkpoint found for session '{Session}'", sessionId);
                return;
            }

            var (key, data, tokenCount) = nearest.Value;
            _logger.LogInformation(
                "MemoryCaching: restored from checkpoint '{Key}' " +
                "(session={Session}, tokens={Tokens}→{CurrentTokens})",
                key, sessionId, tokenCount, currentTokens);

            // Store restoration info in context for other steps
            context.Set("_restoredFromCheckpoint", key);
            context.Set("_restoredTokenPos", tokenCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryCaching: restore failed for session '{Session}'", sessionId);
        }
    }

    /// <summary>Build a compact checkpoint blob from context state.</summary>
    private static byte[] BuildCheckpointData(MessageContext ctx, string sessionId, long tokenCount)
    {
        var summary = new System.Text.StringBuilder();
        summary.Append('{');
        summary.Append("\"session\":\"").Append(sessionId).Append('"');
        summary.Append(",\"tokens\":").Append(tokenCount);
        summary.Append(",\"msgCount\":").Append(ctx.Messages.Count);
        summary.Append(",\"toolCalls\":").Append(ctx.ToolCalls.Count);

        // Include last message text (truncated to 200 chars for reference)
        var lastMsg = ctx.Messages.LastOrDefault()?.Text ?? "";
        if (lastMsg.Length > 200) lastMsg = lastMsg[..200];
        summary.Append(",\"lastMsg\":\"").Append(System.Text.Json.JsonEncodedText.Encode(lastMsg)).Append('"');

        // Include a compact message count summary: roles
        summary.Append(",\"roles\":[");
        bool first = true;
        foreach (var m in ctx.Messages)
        {
            if (!first) summary.Append(',');
            summary.Append('"').Append(m.Role).Append('"');
            first = false;
        }
        summary.Append(']');

        summary.Append('}');
        return System.Text.Encoding.UTF8.GetBytes(summary.ToString());
    }

    /// <summary>Estimate token count from conversation messages.</summary>
    private static long EstimateTokenCount(MessageContext ctx)
    {
        long count = 0;
        foreach (var msg in ctx.Messages)
        {
            count += (msg.Text?.Length ?? 0) / 2; // rough: 2 chars ≈ 1 token
            if (msg.Contents != null)
                count += msg.Contents.Count * 10; // rough: 10 tokens per content item
        }
        return count;
    }
}
