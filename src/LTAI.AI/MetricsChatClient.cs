// Copyright (c) LTAI. All rights reserved.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public sealed class MetricsChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ILogger<MetricsChatClient>? _logger;

    private static readonly Meter Meter = new("LTAI.AI");
    private static readonly Counter<long> TotalTokens = Meter.CreateCounter<long>("llm.tokens", "tokens", "Total LLM tokens");
    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("llm.input_tokens", "tokens", "LLM input tokens");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("llm.output_tokens", "tokens", "LLM output tokens");
    private static readonly Histogram<double> LatencyMs = Meter.CreateHistogram<double>("llm.latency_ms", "ms", "LLM call latency");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("llm.errors", "errors", "LLM call errors");

    public MetricsChatClient(IChatClient inner, ILogger<MetricsChatClient>? logger = null)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            sw.Stop();
            var input = (int)(response.Usage?.InputTokenCount ?? 0);
            var output = (int)(response.Usage?.OutputTokenCount ?? 0);
            if (input > 0) InputTokens.Add(input);
            if (output > 0) OutputTokens.Add(output);
            if (input + output > 0) TotalTokens.Add(input + output);
            LatencyMs.Record(sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            Errors.Add(1);
            _logger?.LogWarning(ex, "LLM call failed");
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        long tokenEstimate = 0;
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(update.Text))
                tokenEstimate += update.Text.Length / 4;
            yield return update;
        }
        sw.Stop();
        if (tokenEstimate > 0) TotalTokens.Add(tokenEstimate);
        LatencyMs.Record(sw.ElapsedMilliseconds);
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) => _inner.GetService(serviceType, serviceKey);
    void IDisposable.Dispose() => _inner.Dispose();
}
