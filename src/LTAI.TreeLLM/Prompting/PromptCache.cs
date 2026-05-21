using LTAI.Core.Acceleration;
using LTAI.Core.System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.TreeLLM.Prompting;

public sealed class PromptCache
{
    private readonly IChatClient _fallbackClient;
    private readonly ResponseCache _responseCache;
    private readonly ILogger<PromptCache> _logger;
    private long _hits;
    private long _misses;
    private long _tokensSaved;

    public PromptCache(
        IChatClient fallbackClient,
        ILogger<PromptCache>? logger = null)
    {
        _fallbackClient = fallbackClient;
        _responseCache = ResponseCache.Instance;
        _logger = logger ?? NullLogger<PromptCache>.Instance;
    }

    public async Task<string> GetOrComputeAsync(
        string prompt,
        string model,
        CancellationToken cancellationToken = default)
    {
        var exact = _responseCache.Get(prompt, model);
        if (exact != null)
        {
            Interlocked.Increment(ref _hits);
            Interlocked.Add(ref _tokensSaved, ExtractTokenCount(exact));
            _logger.LogDebug("PromptCache: exact hit for {Model}", model);
            return exact;
        }

        Interlocked.Increment(ref _misses);
        var response = await _fallbackClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        var answer = response.Text ?? string.Empty;
        _responseCache.Set(prompt, answer, model);
        return answer;
    }

    public async Task<ChatResponse> GetResponseCachedAsync(
        string prompt,
        string model,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = await GetOrComputeAsync(prompt, model, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingCachedAsync(
        string prompt,
        string model,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamCachedAsync(prompt, model, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamCachedAsync(
        string prompt,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var text = await GetOrComputeAsync(prompt, model, cancellationToken);
        const int chunkSize = 8;
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[i..Math.Min(i + chunkSize, text.Length)]);
        }
    }

    public double HitRate
    {
        get
        {
            var total = Interlocked.Read(ref _hits) + Interlocked.Read(ref _misses);
            return total == 0 ? 0.0 : (double)Interlocked.Read(ref _hits) / total;
        }
    }

    public void Invalidate(string? prompt = null, string? model = null)
    {
        _responseCache.Invalidate(prompt, model);
    }

    public Dictionary<string, object> Stats()
    {
        return new()
        {
            ["hits"] = Interlocked.Read(ref _hits),
            ["misses"] = Interlocked.Read(ref _misses),
            ["hit_rate"] = Math.Round(HitRate, 3),
            ["tokens_saved"] = Interlocked.Read(ref _tokensSaved),
            ["response_entries"] = _responseCache.Entries.Count
        };
    }

    private static int ExtractTokenCount(string text) =>
        TokenCounter.Estimate(text);
}
