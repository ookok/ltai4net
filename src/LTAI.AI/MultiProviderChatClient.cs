using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Multi-LLM provider router with automatic degradation chain and circuit breaker.
/// Delegates to <see cref="ProviderClientManager"/>, <see cref="CircuitBreakerManager"/>,
/// and <see cref="ResponseCacheManager"/> for sub-domain concerns.
///
/// Degradation flow: DeepSeek (L1 flash) → DeepSeek-pro (L2) → other registered providers.
/// Circuit breaker: 3 consecutive failures → 30s cooldown per provider.
/// Auth/payment errors (401/403/402) → permanent ban for the session.
/// </summary>
public sealed class MultiProviderChatClient : IChatClient
{
    private readonly ProviderClientManager _providers;
    private readonly CircuitBreakerManager _breaker;
    private readonly ResponseCacheManager _responseCache;
    private readonly ILogger<MultiProviderChatClient> _logger;
    private readonly int _perProviderTimeoutSec;
    private volatile string? _lastError;
    private readonly ConcurrentDictionary<string, bool> _thinkingProviders = new(StringComparer.OrdinalIgnoreCase);

    // LLM call counter
    private static long _callCounter;

    // OpenTelemetry instruments
    private static readonly Meter LltMeter = new("LTAI.AI.Router");
    private static readonly Counter<long> LltCalls = LltMeter.CreateCounter<long>("ltai.llm.calls", "calls", "Total LLM API calls");
    private static readonly Histogram<double> LltDuration = LltMeter.CreateHistogram<double>("ltai.llm.duration", "ms", "LLM call duration");
    private static readonly Counter<long> LltErrors = LltMeter.CreateCounter<long>("ltai.llm.errors", "errors", "LLM call errors");
    private static readonly Histogram<long> LltTokenUsage = LltMeter.CreateHistogram<long>("ltai.llm.token_usage", "tokens", "LLM token usage per call");

    public IEnumerable<string> RegisteredProviders => _providers.RegisteredProviders;
    public string? ActiveProvider { get => _providers.ActiveProvider; set => _providers.ActiveProvider = value; }

    public IChatClient GetL3Client() => _providers.GetL3Client();
    public IChatClient GetL2Client() => _providers.GetL2Client();

    public MultiProviderChatClient(
        LTAIOptions options,
        ProviderClientManager providerManager,
        CircuitBreakerManager breakerManager,
        ResponseCacheManager responseCache,
        CircuitBreakerStore? breakerStore = null,
        ModelMetadataProvider? modelMetadata = null,
        ILogger<MultiProviderChatClient>? logger = null)
    {
        var esc = options.Escalation;
        _perProviderTimeoutSec = esc.PerProviderTimeoutSeconds;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MultiProviderChatClient>.Instance;
        _providers = providerManager;
        _breaker = breakerManager;
        _responseCache = responseCache;

        if (options.AI.DegradationChain != null)
        {
            foreach (var (k, v) in options.AI.DegradationChain)
            {
                _providers.Register(k, null!); // seed chain; clients registered via Register()
                // Also set degradation chain on ProviderClientManager if not already configured
                if (!_providers.HasDegradation(k))
                    _providers.SetDegradation(k, v);
            }
        }
    }

    /// <summary>Register a named IChatClient instance.</summary>
    public void Register(string name, IChatClient client, bool enableThinking = false)
    {
        _providers.Register(name, client);
        if (enableThinking)
            _thinkingProviders[name] = true;
    }

    /// <summary>Register an edge (local inference) IChatClient. Edge providers are tried first in routing.</summary>
    public void RegisterEdge(string name, IChatClient client)
    {
        _providers.RegisterEdge(name, client);
    }

    public string ResolveProvider(ChatOptions? options) => _providers.ResolveProvider(options);

    public IEnumerable<string> RankedProviders(string preferred) => _providers.RankedProviders(preferred);

    public ChatClientMetadata? Metadata => new("MultiProvider", new Uri("https://github.com/ltai-org/ltai4net"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var provider = ResolveProvider(options);
        // Clear ModelId after resolving provider to prevent downstream confusion.
        // Intentional mutable side-effect: callers should not reuse ChatOptions across calls.
        if (options != null) options.ModelId = null;

        var sw = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await TryCallWithDegradation(provider, messages, options, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            sw.Stop();
            LltErrors.Add(1);
            LltDuration.Record(sw.ElapsedMilliseconds);
            throw;
        }

        sw.Stop();
        LltCalls.Add(1);
        LltDuration.Record(sw.ElapsedMilliseconds);
        if (response.Usage is { } usage)
        {
            var totalTokens = (int)(usage.InputTokenCount ?? 0) + (int)(usage.OutputTokenCount ?? 0);
            if (totalTokens > 0)
                LltTokenUsage.Record(totalTokens);
        }
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = ResolveProvider(options);
        // Clear ModelId after resolving provider to prevent downstream confusion.
        // Intentional mutable side-effect: callers should not reuse ChatOptions across calls.
        if (options != null) options.ModelId = null;
        bool anyAttempted = false;
        string? lastFailedProvider = null;
        var sw = Stopwatch.StartNew();
        long estimatedTokens = 0;

        var streamEstimatedTotal = _providers.EstimateContextTokens(messages);

        foreach (var p in _providers.RankedProviders(provider))
        {
            var client = _providers.GetClient(p);
            if (client == null) continue;
            var callNum = Interlocked.Increment(ref _callCounter);
            _logger.LogDebug("LLM streaming call #{CallNum} → provider={Provider}", callNum, p);

            anyAttempted = true;
            var success = false;

            // Dedup tools
            options = DedupTools(options);
            options = ApplyThinkingOptions(options, p);

            // Notify of fallback
            if (lastFailedProvider != null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    $"\n\n_[Stream from '{lastFailedProvider}' failed midway, falling back to '{p}']_\n\n");
            }

            // Pre-flight context window check
            {
                var ctxLimit = UsageTracker.ResolveContextWindow(p);
                if (ctxLimit > 0 && streamEstimatedTotal > ctxLimit * 0.95)
                {
                    _logger.LogWarning("Pre-flight streaming: estimated context {Est}/{Limit} tokens exceeds 95% of window for '{P}'. Skipping.", streamEstimatedTotal, ctxLimit, p);
                    lastFailedProvider ??= p;
                    continue;
                }
            }

            using var streamingTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            streamingTimeoutCts.CancelAfter(TimeSpan.FromSeconds(_perProviderTimeoutSec));
            var timeoutToken = streamingTimeoutCts.Token;
            var enumerable = client.GetStreamingResponseAsync(messages, options, timeoutToken);
            var enumerator = enumerable.GetAsyncEnumerator(timeoutToken);
            await using (enumerator.ConfigureAwait(false))
            {
                success = true;
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _lastError = $"Streaming timeout ({_perProviderTimeoutSec}s)";
                        _logger.LogWarning("Streaming from '{P}' timed out after {S}s, degrading", p, _perProviderTimeoutSec);
                        lastFailedProvider = p;
                        success = false;
                        _breaker.RecordFailure(p);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        _logger.LogWarning(ex, "Streaming from '{P}' failed, degrading", p);
                        lastFailedProvider = p;
                        success = false;
                        _breaker.RecordFailure(p);
                        break;
                    }
                    success = true;
                    if (!string.IsNullOrWhiteSpace(enumerator.Current.Text))
                        estimatedTokens += enumerator.Current.Text.Length / 4;
                    yield return enumerator.Current;
                }
            }
            if (success)
            {
                sw.Stop();
                LltCalls.Add(1);
                LltDuration.Record(sw.ElapsedMilliseconds);
                if (estimatedTokens > 0) LltTokenUsage.Record(estimatedTokens);
                _logger.LogDebug("Streaming succeeded from '{P}'", p);
                yield break;
            }
        }
        sw.Stop();
        LltErrors.Add(1);
        LltDuration.Record(sw.ElapsedMilliseconds);
        yield return new ChatResponseUpdate(ChatRole.Assistant,
            anyAttempted
                ? $"All providers failed for '{provider}'. Last error: {_lastError ?? "(unknown)"}"
                : $"No providers available for '{provider}'");
    }

    public async Task<ChatResponse?> TryProviderAsync(
        string provider, List<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var client = _providers.GetClient(provider);
        if (client == null) return null;
        if (_breaker.IsInCooldown(provider)) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_perProviderTimeoutSec));
            return await client.GetResponseAsync(messages, options, timeoutCts.Token).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private async Task<ChatResponse> TryCallWithDegradation(
        string provider, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var cacheKey = ResponseCacheManager.BuildCacheKey(provider, messages, options);
        if (_responseCache.TryGet(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("Cache HIT for provider '{P}', key={Key}", provider, cacheKey);
            UsageTracker.RecordCacheHit();
            ReplayCacheUsage(cached, provider);
            return cached;
        }

        var estimatedTotal = _providers.EstimateContextTokens(messages);

        foreach (var p in _providers.RankedProviders(provider))
        {
            var client = _providers.GetClient(p);
            if (client == null) continue;

            if (_breaker.IsInCooldown(p))
            {
                _logger.LogDebug("Provider '{P}' in cooldown, skipping", p);
                continue;
            }

            try
            {
                options = DedupTools(options);
                options = ApplyThinkingOptions(options, p);

                var callNum = Interlocked.Increment(ref _callCounter);
                var toolCount = options?.Tools?.Count ?? 0;
                var msgCount = messages?.Count() ?? 0;
                var textLen = messages?.Sum(m => m.Text?.Length ?? 0) ?? 0;
                _logger.LogDebug("LLM call #{CallNum} → provider={Provider}, {ToolCount} tools, {MsgCount} msgs, ~{TextLen} chars", callNum, p, toolCount, msgCount, textLen);

                var ctxLimit = UsageTracker.ResolveContextWindow(p);
                if (ctxLimit > 0 && estimatedTotal > ctxLimit * 0.95)
                {
                    _logger.LogWarning("Pre-flight: estimated context {Est}/{Limit} tokens exceeds 95% of window for '{P}'. Skipping.", estimatedTotal, ctxLimit, p);
                    continue;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_perProviderTimeoutSec));

                var result = await client.GetResponseAsync(messages ?? [], options, timeoutCts.Token).ConfigureAwait(false);

                // Track token usage
                if (result.Usage is { } usage)
                {
                    var promptTotal = (int)(usage.InputTokenCount ?? 0);
                    var completion = (int)(usage.OutputTokenCount ?? 0);
                    int cacheHit = 0, cacheMiss = 0;
                    if (usage.AdditionalCounts is { } counts)
                    {
                        cacheHit = counts.TryGetValue("prompt_cache_hit_tokens", out var hit) ? (int)hit
                                 : counts.TryGetValue("Cached", out var cachedHit) ? (int)cachedHit : 0;
                        if (counts.TryGetValue("prompt_cache_miss_tokens", out var apiMiss))
                            cacheMiss = (int)apiMiss;
                    }
                    if (cacheMiss == 0 && cacheHit > 0) cacheMiss = promptTotal - cacheHit;
                    else if (cacheMiss == 0) cacheMiss = promptTotal;
                    UsageTracker.RecordWithCache(promptTotal, completion, cacheHit, cacheMiss, p);
                }

                _responseCache.Set(cacheKey, result);
                _breaker.RecordSuccess(p);
                return result;
            }
            catch (ClientResultException ex) when (ex.Status is
                (int)System.Net.HttpStatusCode.Unauthorized or
                (int)System.Net.HttpStatusCode.Forbidden or
                (int)System.Net.HttpStatusCode.PaymentRequired)
            {
                _lastError = $"HTTP {(int)ex.Status} {ex.Message}";
                _logger.LogWarning("Provider '{P}' permanently banned: {Status}", p, ex.Status);
                _breaker.SetPermanentBan(p);
                _breaker.RecordFailure(p);
                continue;
            }
            catch (ClientResultException ex) when (ex.Status == (int)System.Net.HttpStatusCode.TooManyRequests)
            {
                _lastError = "Rate limited (HTTP 429)";
                var cooldown = TimeSpan.FromSeconds(30);
                _breaker.SetCooldown(p, DateTime.UtcNow + cooldown);
                _logger.LogWarning("Provider '{P}' rate limited, cooldown {Cooldown}s", p, cooldown.TotalSeconds);
                _breaker.RecordFailure(p);
                continue;
            }
            catch (TimeoutException)
            {
                _lastError = $"Timeout after {_perProviderTimeoutSec}s";
                _logger.LogWarning("Provider '{P}' timed out after {S}s, degrading", p, _perProviderTimeoutSec);
                _breaker.RecordFailure(p);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastError = $"Timeout after {_perProviderTimeoutSec}s";
                _logger.LogWarning("Provider '{P}' timed out, degrading", p);
                _breaker.RecordFailure(p);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Provider '{P}' failed, degrading to fallback", p);
                _breaker.RecordFailure(p);
                continue;
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"All providers failed for '{provider}'. Last error: {_lastError ?? "(unknown)"}"));
    }

    private ChatOptions? ApplyThinkingOptions(ChatOptions? options, string provider)
    {
        if (!_thinkingProviders.ContainsKey(provider)) return options;
        if (options == null)
        {
            options = new ChatOptions();
        }
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["enable_thinking"] = true;
        options.AdditionalProperties["thought_in_content"] = true;
        return options;
    }

    private static ChatOptions? DedupTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 10 }) return options;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Build deduplicated tool list preserving original order
        var deduped = new List<AITool>(options.Tools.Count);
        for (int i = options.Tools.Count - 1; i >= 0; i--)
        {
            var tool = options.Tools[i];
            var n = tool.Name;
            if (n != null && seen.Add(n.Trim()))
                deduped.Add(tool);
        }
        deduped.Reverse();

        // Clone all available properties to avoid dropping AdditionalProperties (thinking, etc.)
        return new ChatOptions
        {
            ModelId = options.ModelId,
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            MaxOutputTokens = options.MaxOutputTokens,
            StopSequences = options.StopSequences,
            Seed = options.Seed,
            ResponseFormat = options.ResponseFormat,
            ToolMode = options.ToolMode,
            Tools = new List<AITool>(deduped),
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            AdditionalProperties = options.AdditionalProperties,
        };
    }

    private static void ReplayCacheUsage(ChatResponse cached, string provider)
    {
        if (cached.Usage is { } cachedUsage)
        {
            var promptTotal = (int)(cachedUsage.InputTokenCount ?? 0);
            var completion = (int)(cachedUsage.OutputTokenCount ?? 0);
            int cacheHit = 0, cacheMiss = promptTotal;
            if (cachedUsage.AdditionalCounts is { } counts)
            {
                cacheHit = counts.TryGetValue("prompt_cache_hit_tokens", out var hit) ? (int)hit
                         : counts.TryGetValue("Cached", out var cachedHit) ? (int)cachedHit : 0;
                if (counts.TryGetValue("prompt_cache_miss_tokens", out var apiMiss))
                    cacheMiss = (int)apiMiss;
            }
            if (cacheHit > 0 && cacheMiss == promptTotal) cacheMiss = promptTotal - cacheHit;
            UsageTracker.RecordWithCache(promptTotal, completion, cacheHit, cacheMiss, provider);
        }
        else
        {
            var text = cached.Messages?.LastOrDefault()?.Text ?? "";
            var promptT = TokenEstimator.Estimate(text);
            var completionT = TokenEstimator.Estimate(text) / 2;
            UsageTracker.Record(promptT, completionT, provider);
            UsageTracker.RecordCacheTokens(promptT, 0);
        }
    }

    object? IChatClient.GetService(Type t, object? k) => t == typeof(ChatClientMetadata) ? Metadata : null;

    void IDisposable.Dispose()
    {
        _providers.DisposeClients();
    }
}
