// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RetryQueueWorker — queues failed LLM retries to TaskQueue
//
//  Problem: MultiProviderChatClient.TryCallWithDegradation does
//  synchronous per-provider fallback. Each provider gets 15s
//  timeout before trying the next. For 3 providers = up to 45s
//  of wait that blocks the calling thread.
//
//  Solution: On first provider failure, enqueue the retry as a
//  TaskQueue work item with exponential backoff. The calling
//  thread gets the primary provider's result (or error) immediately;
//  the retry runs in the background if the primary fails.
//
//  Backoff sequence: 1s → 2s → 4s → 8s → 16s → max 5 retries
// ═══════════════════════════════════════════════════════════════

using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

/// <summary>
/// Wraps LLM retry logic behind the TaskQueue. When a provider
/// fails, enqueues a background retry with exponential backoff.
/// </summary>
public sealed class RetryQueueWorker
{
    private static readonly TimeSpan[] BackoffSequence =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    ];

    private readonly LTAI.AI.MultiProviderChatClient _client;
    private readonly LTAI.Agent.Tasks.TaskQueue _queue;
    private readonly ILogger<RetryQueueWorker> _logger;

    public RetryQueueWorker(
        MultiProviderChatClient client,
        LTAI.Agent.Tasks.TaskQueue queue,
        ILogger<RetryQueueWorker> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        // Test reference to suppress unused warning
        _ = _client.RegisteredProviders;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get a response from the primary provider. If it fails, enqueue
    /// a background retry with exponential backoff.
    /// Returns the primary result (success or failure) immediately.
    /// </summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var provider = _client.ResolveProvider(options);
        var messagesList = messages.ToList();

        // Try primary provider directly (fast path)
        var primaryResult = await _client.TryProviderAsync(provider, messagesList, options, ct)
            .ConfigureAwait(false);

        if (primaryResult != null)
            return primaryResult;

        // Primary failed — enqueue background retry
        var traceId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogWarning("RetryQueueWorker: primary provider '{P}' failed, enqueuing retry (trace={T})",
            provider, traceId);

        _ = EnqueueRetryAsync(provider, messagesList, options, traceId, 0, ct)
            .ConfigureAwait(false);

        // Return the primary failure result — retry happens in background
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"[Provider '{provider}' failed — retry enqueued (trace: {traceId})]"));
    }

    private async Task EnqueueRetryAsync(
        string provider,
        List<ChatMessage> messages,
        ChatOptions? options,
        string traceId,
        int attempt,
        CancellationToken ct)
    {
        if (attempt >= BackoffSequence.Length)
        {
            _logger.LogWarning("RetryQueueWorker: exhausted retries for '{P}' (trace={T})",
                provider, traceId);
            return;
        }

        var delay = BackoffSequence[attempt];
        _logger.LogInformation("RetryQueueWorker: retry attempt {A}/{M} for '{P}' in {S}s (trace={T})",
            attempt + 1, BackoffSequence.Length, provider, delay.TotalSeconds, traceId);

        await _queue.EnqueueAsync(
            name: $"llm-retry:{provider}:{traceId}:attempt-{attempt + 1}",
            work: async (innerCt) =>
            {
                // Wait for backoff inside the task
                await Task.Delay(delay, innerCt).ConfigureAwait(false);

                // Try all remaining providers via degradation chain
                foreach (var p in _client.RankedProviders(provider))
                {
                    var result = await _client.TryProviderAsync(p, messages, options, innerCt)
                        .ConfigureAwait(false);

                    if (result != null)
                    {
                        _logger.LogInformation("RetryQueueWorker: retry succeeded on '{P}' (trace={T})",
                            p, traceId);
                        UsageTracker.Record(0, 0, $"retry:{p}");
                        return $"Retry succeeded on '{p}' (trace: {traceId})";
                    }
                }

                // All providers failed — schedule next retry
                _logger.LogWarning("RetryQueueWorker: retry attempt {A} failed for '{P}' (trace={T})",
                    attempt + 1, provider, traceId);
                _ = EnqueueRetryAsync(provider, messages, options, traceId, attempt + 1, ct)
                    .ConfigureAwait(false);
                return $"Retry attempt {attempt + 1} failed — scheduling next (trace: {traceId})";
            },
            ct: ct).ConfigureAwait(false);
    }
}
