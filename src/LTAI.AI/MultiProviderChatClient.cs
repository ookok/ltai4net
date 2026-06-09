using System.ClientModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Anthropic;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;

namespace LTAI.AI;

/// <summary>
/// Multi-LLM provider router with automatic degradation chain and circuit breaker.
/// Auto-registers all providers with valid API keys at startup via KnownKeys.
/// Implements IChatClient to serve as the primary LLM interface for the whole system.
/// Wrapped by <see cref="LTAI.Core.Safety.SafeChatClient"/> for output safety.
///
/// Degradation flow: DeepSeek (L1 flash) → DeepSeek-pro (L2) → other registered providers.
/// Circuit breaker: 3 consecutive failures → 30s cooldown per provider.
/// Auth/payment errors (401/403/402) → permanent ban for the session.
///
/// <b>Consumers:</b> All agents/tools that make LLM calls through IChatClient DI.
/// Registered in AddLTAIAI() in this file.
///
/// ⚠ KNOWN ISSUE: Uses SHA256 for cache key hashing — overkill; XxHash64 would suffice.
/// ⚠ KNOWN ISSUE: Cache-hit path uses text.Length/4 as estimated token count (fake metric).
/// </summary>
public sealed class MultiProviderChatClient : IChatClient
{
    /// <summary>All known providers: (envVar, endpoint, model, displayName).
    /// Generated from <see cref="LTAI.Core.Configuration.KnownKeys.GetDefaultProviders"/> — single source of truth
    /// for all provider configurations across the system.</summary>
    public static readonly (string envVar, string endpoint, string model, string name)[] DefaultProviders =
        LTAI.Core.Configuration.KnownKeys.GetDefaultProviders();

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MultiProviderChatClient> _logger;
    private readonly ModelMetadataProvider? _modelMetadata;
    private string _defaultProvider;
    private readonly string _routingFallback = "l1"; // fallback routing key when no ModelId is set

    // 自适应成本路由：成功率 + 延迟 + 成本感知
    private string? _lastError;
    private readonly ConcurrentDictionary<string, ProviderStats> _providerStats = new(StringComparer.OrdinalIgnoreCase);
    // Circuit breaker state per provider (thread-safe via ConcurrentDictionary)
    // P0: optionally backed by SQLite (CircuitBreakerStore) so cooldown survives process restart
    private readonly ConcurrentDictionary<string, int> _providerFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _providerCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly LTAI.Core.Configuration.CircuitBreakerStore? _breakerStore;
    private readonly Lazy<Task> _breakerLoadTask;
    private const int MaxFailuresBeforeCooldown = 3;
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);

    // Response cache (LRU, 5min TTL) — shared across ALL instances (static)
    private static MemoryCache _responseCache = new(new MemoryCacheOptions
    {
        SizeLimit = 256,
        ExpirationScanFrequency = TimeSpan.FromMinutes(1)
    });
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static int _responseCacheSizeLimit = 256;
    private readonly int _perProviderTimeoutSec = 15;

    // LLM call counter — increments on every actual HTTP request
    private static long _callCounter;

    /// <summary>Names of all currently registered LLM clients.</summary>
    public IEnumerable<string> RegisteredProviders => _clients.Keys;
    /// <summary>Currently active default provider name.</summary>
    public string ActiveProvider { get => _defaultProvider; set => _defaultProvider = value; }

    /// <summary>
    /// Initialize the router. Sets default provider from options and loads degradation chain.
    /// Actual provider clients are registered later via <see cref="Register"/> in AddLTAIAI().
    /// </summary>
    public MultiProviderChatClient(LTAIOptions options,
        ILogger<MultiProviderChatClient>? logger = null,
        LTAI.Core.Configuration.CircuitBreakerStore? breakerStore = null,
        ModelMetadataProvider? modelMetadata = null)
    {
        _defaultProvider = options.AI.DefaultProvider;
        _logger = logger ?? NullLogger<MultiProviderChatClient>.Instance;
        _breakerStore = breakerStore;
        _modelMetadata = modelMetadata;
        if (options.AI.DegradationChain != null)
        {
            foreach (var (k, v) in options.AI.DegradationChain)
                _degradation.TryAdd(k, v);
        }
        if (options.AI.ResponseCacheSize > 0 && options.AI.ResponseCacheSize != 256)
        {
            _responseCacheSizeLimit = options.AI.ResponseCacheSize;
            _responseCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = options.AI.ResponseCacheSize,
                ExpirationScanFrequency = TimeSpan.FromMinutes(1)
            });
        }

        // Restore circuit breaker state from SQLite (cross-restart persistence).
        // Loaded lazily to avoid sync-over-async in constructor.
        _breakerLoadTask = new Lazy<Task>(async () =>
        {
            if (_breakerStore == null) return;
            try
            {
                var all = await _breakerStore.LoadAllAsync().ConfigureAwait(false);
                var now = DateTime.UtcNow;
                foreach (var (provider, (failures, cooldownUntil)) in all)
                {
                    if (failures > 0)
                        _providerFailures[provider] = failures;
                    if (cooldownUntil.HasValue && cooldownUntil.Value > now)
                        _providerCooldowns[provider] = cooldownUntil.Value;
                }
            }
            catch { /* best-effort; in-memory fallback is still functional */ }
        });
        // Fire-and-forget: breaker state loads in background; first LLM call
        // waits for completion via _breakerLoadTask if needed.
        _ = _breakerLoadTask;
    }

    /// <summary>
    /// Register a named IChatClient instance.
    /// <b>Callers:</b> AddLTAIAI() ServiceCollectionExtensions (once per provider with valid API key).
    /// Also clears any existing cooldown/circuit-breaker state so the newly registered client
    /// is immediately usable instead of being blocked by a stale entry.
    /// </summary>
    public void Register(string name, IChatClient client)
    {
        _clients[name] = client; // override: TUI can replace DI-registered stale clients
        _providerCooldowns.TryRemove(name, out _);
        _providerFailures.TryRemove(name, out _);
    }

    /// <summary>
    /// Resolve provider name from options.ModelId.
    /// Supports capability: prefix (e.g. "capability:tool-call") — uses ModelMetadataProvider
    /// to find the best registered provider with that capability.
    /// Falls back to options.ModelId as-is for backward compat.
    /// </summary>
    /// <summary>Resolve provider from options, falling back to <c>AI.DefaultProvider</c>.</summary>
    public string ResolveProvider(ChatOptions? options)
    {
        var raw = options?.ModelId ?? _routingFallback;
        if (raw == null) return _routingFallback;

        const string capabilityPrefix = "capability:";
        if (!raw.StartsWith(capabilityPrefix, StringComparison.OrdinalIgnoreCase))
            return raw;

        var capName = raw[capabilityPrefix.Length..];
        var cap = capName.ToLowerInvariant() switch
        {
            "chat" => ModelCapability.Chat,
            "streaming" or "stream" => ModelCapability.Streaming,
            "tool-call" or "toolcall" or "tools" or "function-call" => ModelCapability.ToolCall,
            "structured-output" or "structured" or "json" => ModelCapability.StructuredOutput,
            "vision" => ModelCapability.Vision,
            _ => ModelCapability.Chat | ModelCapability.Streaming,
        };

        if (_modelMetadata?.RecommendModel(cap, _defaultProvider) is { } recommended)
        {
            // Only use if we actually have this provider registered
            if (_clients.ContainsKey(recommended.Provider))
                return recommended.Provider;
        }

        return _routingFallback;
    }

    /// <summary>Identity metadata for OpenTelemetry instrumentation.</summary>
    public ChatClientMetadata? Metadata => new("MultiProvider", new Uri("https://github.com/ltai-org/ltai4net"));

    /// <summary>
    /// Get a non-streaming response with automatic degradation and circuit breaker.
    /// 1. Check response cache (SHA256 key, 5min TTL)
    /// 2. Iterate degradation chain
    /// 3. Per-provider 15s timeout
    /// 4. Auth/payment errors → permanent ban
    /// 5. Rate limiting → 30s cooldown (uses Retry-After if available)
    /// 6. 3+ consecutive failures → 30s circuit breaker
    /// <b>Callers:</b> System-wide via IChatClient DI — agents, tools, workflows.
    /// </summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var provider = ResolveProvider(options);
        // ModelId was consumed for provider routing; clear it to prevent
        // the underlying IChatClient (OpenAI SDK) from using it as the API model name.
        if (options != null) options.ModelId = null;
        return await TryCallWithDegradation(provider, messages, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get a streaming response with per-provider degradation on mid-stream failure.
    /// If streaming fails midway on one provider, switches to the next in degradation chain
    /// and inserts a notice in the stream about the switch-over.
    /// <b>Callers:</b> System-wide via IChatClient DI.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = ResolveProvider(options);
        if (options != null) options.ModelId = null;
        bool anyAttempted = false;
        string? lastFailedProvider = null;
        foreach (var p in RankedProviders(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;
            var callNum = Interlocked.Increment(ref _callCounter);
            _logger.LogInformation("LLM streaming call #{CallNum} → provider={Provider}", callNum, p);

            anyAttempted = true;
            var success = false;

            // Dedup tools (same as non-streaming path — streaming path bypasses TryCallWithDegradation)
            if (options?.Tools is { Count: > 10 })
            {
                var before = options.Tools.Count;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deduped = new List<AITool>(options.Tools.Count);
                int duplicates = 0;
                for (int i = options.Tools.Count - 1; i >= 0; i--)
                {
                    var n = options.Tools[i].Name.Trim();
                    if (seen.Add(n))
                        deduped.Add(options.Tools[i]);
                    else
                        duplicates++;
                }
                deduped.Reverse();
                options.Tools = new List<AITool>(deduped);
                _logger.LogDebug("Streaming dedup: {Before} → {After} tools ({Dups} duplicates)", before, deduped.Count, duplicates);
            }

            // Notify user of fallback switch-over
            if (lastFailedProvider != null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    $"\n\n_[Stream from '{lastFailedProvider}' failed midway, falling back to '{p}']_\n\n");
            }

            using var streamingTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            streamingTimeoutCts.CancelAfter(TimeSpan.FromSeconds(_perProviderTimeoutSec));
            var timeoutToken = streamingTimeoutCts.Token;
            var innerStream = client.GetStreamingResponseAsync(messages, options, timeoutToken);
            await using (var enumerator = innerStream.GetAsyncEnumerator(timeoutToken))
            {
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
                        break;
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        _logger.LogWarning(ex, "Streaming from '{P}' failed, degrading", p);
                        lastFailedProvider = p;
                        break;
                    }
                    success = true;
                    yield return enumerator.Current;
                }
            }
            if (success)
            {
                _logger.LogDebug("Streaming succeeded from '{P}'", p);
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant,
            anyAttempted
                ? $"All providers failed for '{provider}'. Last error: {_lastError ?? "(unknown)"}"
                : $"No providers available for '{provider}'");
    }

    /// <summary>Try a specific provider directly (no degradation). Returns null on failure.</summary>
    public async Task<ChatResponse?> TryProviderAsync(
        string provider, List<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        if (!_clients.TryGetValue(provider, out var client)) return null;

        if (_providerCooldowns.TryGetValue(provider, out var cooldownUntil) && cooldownUntil > DateTime.UtcNow)
            return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            return await client.GetResponseAsync(messages, options, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ChatResponse> TryCallWithDegradation(
        string provider, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        // Check response cache first
        var cacheKey = BuildCacheKey(provider, messages, options);
        if (_responseCache.TryGetValue<ChatResponse>(cacheKey, out var cached))
        {
            _logger.LogDebug("Cache HIT for provider '{P}', key={Key}", provider, cacheKey);
            LTAI.Core.Configuration.UsageTracker.RecordCacheHit();
            // Replay the cached response's actual usage data
            if (cached!.Usage is { } cachedUsage)
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
                LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                    promptTotal, completion, cacheHit, cacheMiss, provider);
            }
            else
            {
                // Fallback: estimate from text length
                var text = cached!.Messages?.LastOrDefault()?.Text ?? "";
                var promptT = text.Length / 4;
                var completionT = text.Length / 8;
                LTAI.Core.Configuration.UsageTracker.Record(promptT, completionT, provider);
                LTAI.Core.Configuration.UsageTracker.RecordCacheTokens(promptT, 0);
            }
            return cached;
        }

        foreach (var p in RankedProviders(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;

            // Circuit breaker: skip providers in cooldown
            if (_providerCooldowns.TryGetValue(p, out var cooldownUntil) && cooldownUntil > DateTime.UtcNow)
            {
                _logger.LogDebug("Provider '{P}' in cooldown until {Cooldown}, skipping", p, cooldownUntil);
                continue;
            }

            try
            {
                // Remove any duplicate tools (Harness may inflate the tool list).
                // Clone the list before mutating to avoid corrupting shared ChatOptions.
                if (options?.Tools is { Count: > 10 })
                {
                    var before = options.Tools.Count;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var deduped = new List<AITool>(options.Tools.Count);
                    int duplicates = 0;
                    for (int i = options.Tools.Count - 1; i >= 0; i--)
                    {
                        var n = options.Tools[i].Name.Trim();
                        if (seen.Add(n))
                            deduped.Add(options.Tools[i]);
                        else
                            duplicates++;
                    }
                    deduped.Reverse();
                    options.Tools = new List<AITool>(deduped);
                    _logger.LogDebug("Non-streaming dedup: {Before} → {After} tools ({Dups} duplicates)", before, deduped.Count, duplicates);
                }

                var callNum = Interlocked.Increment(ref _callCounter);
                var toolCount = options?.Tools?.Count ?? 0;
                var msgCount = messages?.Count() ?? 0;
                var textLen = messages?.Sum(m => m.Text?.Length ?? 0) ?? 0;
                _logger.LogInformation("LLM call #{CallNum} → provider={Provider}, {ToolCount} tools, {MsgCount} msgs, ~{TextLen} chars text", callNum, p, toolCount, msgCount, textLen);

                // Add 15s per-provider timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                var result = await client.GetResponseAsync(messages, options, timeoutCts.Token)
                    .ConfigureAwait(false);

                // Track token usage from MAF-compliant IChatClient.Usage metadata
                if (result.Usage is { } usage)
                {
                    var promptTotal = (int)(usage.InputTokenCount ?? 0);
                    var completion = (int)(usage.OutputTokenCount ?? 0);

                    // DeepSeek 返回 prompt_cache_hit_tokens / prompt_cache_miss_tokens
                    int cacheHit = 0, cacheMiss = 0;
                    if (usage.AdditionalCounts is { } counts)
                    {
                        cacheHit = counts.TryGetValue("prompt_cache_hit_tokens", out var hit) ? (int)hit
                                 : counts.TryGetValue("Cached", out var cachedHit) ? (int)cachedHit : 0;
                        if (counts.TryGetValue("prompt_cache_miss_tokens", out var apiMiss))
                            cacheMiss = (int)apiMiss;
                    }

                    // 从 API 返回推导：若 cache_miss 未显式返回，从总量减去 cache_hit
                    if (cacheMiss == 0 && cacheHit > 0)
                        cacheMiss = promptTotal - cacheHit;
                    else if (cacheMiss == 0)
                        cacheMiss = promptTotal;

                    LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                        promptTotal, completion, cacheHit, cacheMiss, p);
                }

                // Store in cache (miss path)
                _responseCache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = CacheTtl
                });

                // Success — reset failure count, update stats
                _providerFailures.TryRemove(p, out _);
                _providerCooldowns.TryRemove(p, out _);
                if (_breakerStore != null)
                    _ = _breakerStore.ClearAsync(p);
                var stats = _providerStats.GetOrAdd(p, static _ => new ProviderStats());
                Interlocked.Increment(ref stats.SuccessfulCalls);

                return result;
            }
            catch (ClientResultException ex) when (ex.Status is
                (int)System.Net.HttpStatusCode.Unauthorized or
                (int)System.Net.HttpStatusCode.Forbidden or
                (int)System.Net.HttpStatusCode.PaymentRequired)
            {
                // Auth / payment failure — ban permanently (never retry this session)
                _lastError = $"HTTP {(int)ex.Status} {ex.Message}";
                _logger.LogWarning("Provider '{P}' permanently banned: {Status}", p, ex.Status);
                _providerCooldowns[p] = DateTime.MaxValue;
                RecordFailure(p);
                continue;
            }
            catch (ClientResultException ex) when (ex.Status == (int)System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited — honor Retry-After header when present
                _lastError = "Rate limited (HTTP 429)";
                var cooldown = TimeSpan.FromSeconds(30);
                _providerCooldowns[p] = DateTime.UtcNow + cooldown;
                _logger.LogWarning("Provider '{P}' rate limited, cooldown {Cooldown}s", p, cooldown.TotalSeconds);
                RecordFailure(p);
                continue;
            }
            catch (TimeoutException)
            {
                _lastError = "Timeout after 15s";
                _logger.LogWarning("Provider '{P}' timed out after 15s, degrading", p);
                RecordFailure(p);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastError = "Timeout after 15s";
                _logger.LogWarning("Provider '{P}' timed out, degrading", p);
                RecordFailure(p);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Provider '{P}' failed, degrading to fallback", p);
                RecordFailure(p);
                continue;
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"All providers failed for '{provider}'. Last error: {_lastError ?? "(unknown)"}"));
    }

    private void RecordFailure(string provider)
    {
        var count = _providerFailures.AddOrUpdate(provider, 1, (_, c) => c + 1);
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        Interlocked.Increment(ref stats.FailedCalls);
        DateTime? until = null;
        if (count >= MaxFailuresBeforeCooldown)
        {
            until = DateTime.UtcNow + CooldownDuration;
            _providerCooldowns[provider] = until.Value;
            _logger.LogWarning("Provider '{P}' failed {Count} times — cooling down until {Until}",
                provider, count, until);
        }
        // Persist circuit breaker state to SQLite (cross-restart)
        if (_breakerStore != null)
        {
            _ = _breakerStore.SaveAsync(provider, count, until);
        }
    }

    /// <summary>
    /// Build a cache key from provider, messages, and options.
    /// Uses HashCode (built-in, zero extra allocations) for in-memory 256-entry LRU.
    /// </summary>
    private static string BuildCacheKey(string provider, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var hc = new HashCode();
        hc.Add(provider, StringComparer.OrdinalIgnoreCase);
        hc.Add(options?.Temperature);
        hc.Add(options?.MaxOutputTokens);
        foreach (var m in messages)
            hc.Add(m.Text ?? "");
        return hc.ToHashCode().ToString("x8");
    }

    /// <summary>
    /// 沿配置的降级链依次返回可用的 provider，不串到无关 provider。
    /// 降级链如：deepseek → deepseek-pro → (无配置则结束)。
    /// 不在降级链中的 provider 不进入选择列表。
    /// 当降级链耗尽时，如果注入 ModelMetadataProvider，用 RecommendModel
    /// 寻找支持 Chat|Streaming 的已注册 provider 作为宽泛回退。
    /// </summary>
    /// <summary>Ranked provider list with circuit-breaker filtering.</summary>
    public IEnumerable<string> RankedProviders(string preferred)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = preferred;

        while (current != null && seen.Add(current))
        {
            // 跳过冷却中的 provider
            if (_providerCooldowns.TryGetValue(current, out var until) && until > DateTime.UtcNow)
            {
                _logger.LogDebug("Provider '{P}' in cooldown, skipping in degradation chain", current);
                current = _degradation.TryGetValue(current, out var next) ? next : null;
                continue;
            }

            if (_clients.ContainsKey(current))
            {
                yield return current;
            }

            current = _degradation.TryGetValue(current, out var next2) ? next2 : null;
        }

        // 硬编码降级链耗尽 → 利用 ModelMetadataProvider 宽泛回退
        if (_modelMetadata != null)
        {
            foreach (var p in FallbackProviders(preferred, seen))
                yield return p;
        }
    }

    /// <summary>宽泛回退：用 RecommendModel 找支持 Chat|Streaming 的已注册 provider。</summary>
    private IEnumerable<string> FallbackProviders(string preferred, HashSet<string> seen)
    {
        var recommended = _modelMetadata!.RecommendModel(
            ModelCapability.Chat | ModelCapability.Streaming, preferred);
        if (recommended != null && seen.Add(recommended.Value.Provider) &&
            _clients.ContainsKey(recommended.Value.Provider))
        {
            _logger.LogInformation("Fallback: recommending provider '{P}' model '{M}'",
                recommended.Value.Provider, recommended.Value.Model);
            // Also yield the full degradation chain of the recommended provider
            var chain = recommended.Value.Provider;
            while (chain != null && seen.Add(chain))
            {
                if (_clients.ContainsKey(chain)) yield return chain;
                chain = _degradation.TryGetValue(chain, out var next) ? next : null;
            }
        }
    }

    /// <summary>
    /// 健康评分 0.0-1.0。考量因素：
    ///   - 成功率 (最近成功/总尝试) × 0.6
    ///   - 非冷却状态 × 0.4
    /// </summary>
    private double CalcHealthScore(string provider, DateTime now)
    {
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        var successRate = stats.TotalAttempts > 0
            ? (double)stats.SuccessfulCalls / stats.TotalAttempts
            : 0.8; // 新提供商初始评分 0.8
        var notInCooldown = _providerCooldowns.TryGetValue(provider, out var until) && until > now ? 0.0 : 1.0;
        return successRate * 0.6 + notInCooldown * 0.4;
    }

    private sealed record ProviderStats
    {
        public long SuccessfulCalls;
        public long FailedCalls;
        public long TotalAttempts => SuccessfulCalls + FailedCalls;
        #pragma warning disable CS0649 // 预留字段
        public long TotalCostTokens;
#pragma warning restore CS0649
    }

    object? IChatClient.GetService(Type t, object? k) => t == typeof(ChatClientMetadata) ? Metadata : null;
    void IDisposable.Dispose() { foreach (var c in _clients.Values) c.Dispose(); }
}

/// <summary>
/// Factory for creating MAF-compatible <see cref="IChatClient"/> instances
/// against any OpenAI-compatible endpoint (DeepSeek, OpenAI, Groq, SiliconFlow, etc.).
/// Replaces the previous 411-line <c>OpenAiHttpClient</c> self-rolled implementation
/// with the official <c>OpenAIClient</c> + MAF's <c>AsIChatClient()</c> extension.
///
/// <b>Consumers:</b>
/// - <c>MultiProviderChatClient</c> (main LLM router)
/// - <c>AddLTAIAI()</c> (safety LLM)
/// - TUI slash commands and LLM config panel
/// - Desktop MainWindow (test connection)
/// </summary>
public static class OpenAIChatClientFactory
{
    /// <summary>
    /// Create an <see cref="IChatClient"/> for an OpenAI-compatible endpoint.
    /// The OpenAIClient manages its own HttpClient with built-in connection pooling.
    /// </summary>
    /// <param name="endpoint">Base URL, e.g. "https://api.deepseek.com/v1". Trailing slash is trimmed.</param>
    /// <param name="model">Model name, e.g. "deepseek-v4-flash".</param>
    /// <param name="apiKey">Bearer token.</param>
    public static IChatClient Create(
        string endpoint,
        string model,
        string apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.TrimEnd('/'))
        };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }
}

/// <summary>
/// Factory for creating MAF-compatible <see cref="IChatClient"/> instances
/// against the Anthropic Messages API (claude-3-5-sonnet, claude-sonnet-4-5, etc.).
///
/// <b>Consumers:</b>
/// - <c>MultiProviderChatClient</c> (when LTAIOptions registers an Anthropic provider)
/// </summary>
public static class AnthropicChatClientFactory
{
    /// <summary>
    /// Create an <see cref="IChatClient"/> for the Anthropic Messages API.
    /// </summary>
    /// <param name="model">Model name, e.g. "claude-sonnet-4-5" or "claude-haiku-4-5".</param>
    /// <param name="apiKey">Anthropic API key (sk-ant-...).</param>
    /// <param name="defaultMaxTokens">Default max tokens. Anthropic requires this. Defaults to 4096.</param>
    public static IChatClient Create(
        string model,
        string apiKey,
        int? defaultMaxTokens = null)
    {
        return new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey })
            .AsIChatClient(model, defaultMaxTokens ?? 4096);
    }
}

/// <summary>
/// DI registration for the LTAI.AI layer.
/// Registers:
///   - Named HttpClient "llm" with connection pooling (3 conn/server, 2min lifetime)
///   - MultiProviderChatClient (as singleton, not IChatClient — wrapped below)
///   - IChatClient wrapped in SafeChatClient
///   - LocalEmbedder (ONNX BGE-small-zh)
///   - EmbeddingClient (API → local ONNX fallback)
///
/// <b>Callers:</b> Top-level host setup (CLI/TUI/Desktop/Host Program.cs).
///
/// ⚠ KNOWN ISSUE: Safety LLM uses httpFactory.CreateClient() (unnamed, no pooling)
/// while the main LLM uses named "llm" client with pooling. Minor perf loss.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        // Named HttpClient with connection pooling for LLM API calls
        // Reuses TCP+TLS connections to avoid ~200ms handshake overhead per request
        services.AddHttpClient("llm")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 6,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                // 启用自动解压（API 返回可能为 gzip）
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            });

        // Step 1: ModelMetadataProvider — queries all configured providers' /v1/models API,
        // collects context window, capabilities, and pricing. Used for adaptive model selection,
        // TUI /models command, and DevUI dashboard. Background refresh every 15 min.
        // Must be registered before MultiProviderChatClient so the router can use it.
        services.AddSingleton<ModelMetadataProvider>(sp =>
        {
            var provider = new ModelMetadataProvider(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<ModelMetadataProvider>>());
            Task.Run(async () =>
            {
                try
                {
                    await provider.RefreshAllAsync().ConfigureAwait(false);
                    provider.StartBackgroundRefresh();
                }
                catch (Exception ex)
                {
                    var log = sp.GetService<Microsoft.Extensions.Logging.ILogger<ModelMetadataProvider>>();
                    log?.LogWarning(ex, "Model metadata refresh failed at startup");
                }
            });
            return provider;
        });

        // Step 2: Register the raw MultiProviderChatClient (not as IChatClient — we'll wrap it)
        services.AddSingleton<MultiProviderChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            // 用 appsettings.json LTAI:Providers 覆盖硬编码的 KnownKeys.All
            if (opts.Providers.Length > 0)
                LTAI.Core.Configuration.KnownKeys.ApplyConfig(opts.Providers);
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var modelMetadata = sp.GetService<ModelMetadataProvider>();
            var breakerPath = opts.ResolveDataPath("circuit_breaker.db");
            var breakerStore = new LTAI.Core.Configuration.CircuitBreakerStore(breakerPath);
            var router = new MultiProviderChatClient(opts, logger, breakerStore, modelMetadata);

            // Build provider metadata lookup from KnownKeys (name → endpoint, model, envVar)
            var knownProviders = MultiProviderChatClient.DefaultProviders
                .ToDictionary(p => p.name, p => (p.endpoint, p.model, p.envVar), StringComparer.OrdinalIgnoreCase);

            // Only register providers explicitly configured in L1/L2 layers (L0 is embedding).
            // Other providers with API keys are NOT auto-registered — no automatic fallback.
            // Reads from appsettings.json via LTAIOptions.AI.L1/L2 (single config file).
            var l1Cfg = opts.AI.L1; var l2Cfg = opts.AI.L2;
            foreach (var (layerKey, layerCfg) in new[] {
                ("l1", l1Cfg), ("l2", l2Cfg) })
            {
                // Fallback: use DefaultProvider from KnownKeys if layer is unset
                if (layerCfg == null || string.IsNullOrEmpty(layerCfg.Provider))
                {
                    var fb = MultiProviderChatClient.DefaultProviders
                        .FirstOrDefault(p => string.Equals(p.name, opts.AI.DefaultProvider, StringComparison.OrdinalIgnoreCase));
                    if (fb.name == null) continue;
                    var fbKey = SecretManager.Get(fb.envVar) ?? "";
                    router.Register(layerKey, OpenAIChatClientFactory.Create(fb.endpoint, fb.model, fbKey));
                    logger?.LogInformation("Registered fallback {Layer} → {Provider}/{Model}", layerKey, fb.name, fb.model);
                    continue;
                }
                if (!knownProviders.TryGetValue(layerCfg.Provider, out var info))
                {
                    logger?.LogWarning("Layer {Layer} provider '{Provider}' not found in known providers", layerKey, layerCfg.Provider);
                    continue;
                }

                var apiKey = SecretManager.Get(info.envVar) ?? "";
                var model = !string.IsNullOrEmpty(layerCfg.Model) ? layerCfg.Model : info.model;
                var ep = !string.IsNullOrEmpty(layerCfg.Endpoint) ? layerCfg.Endpoint : info.endpoint;
                var isAnthropic = string.Equals(layerCfg.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase);
                var client = isAnthropic
                    ? AnthropicChatClientFactory.Create(model, apiKey)
                    : OpenAIChatClientFactory.Create(ep, model, apiKey);
                router.Register(layerKey, client);
                logger?.LogInformation("Registered layer {Layer} → provider={Provider} model={Model}", layerKey, layerCfg.Provider, model);
            }
            return router;
        });

        // Step 2b: Wrap with SafeChatClient for output safety interception (optional)
        services.AddSingleton<IChatClient>(sp =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;

            if (opts.AI.SkipSafetyChecks)
                return router; // Bypass safety in dev mode

            var logger = sp.GetService<ILogger<LTAI.Core.Safety.SafeChatClient>>();

            // 优雅降级：safety 模型未配置时不抛异常，跳过 safety wrapper
            // 优先级: opts.AI.Model -> L1.Model -> KnownKeys 默认模型
            var safetyModel = !string.IsNullOrEmpty(opts.AI.Model)
                ? opts.AI.Model
                : opts.AI.L1?.Model;

            if (string.IsNullOrEmpty(safetyModel))
            {
                // 尝试从 KnownKeys 默认 provider 取模型名作为 fallback
                var defaultProvider = MultiProviderChatClient.DefaultProviders
                    .FirstOrDefault(p => string.Equals(p.name, opts.AI.DefaultProvider, StringComparison.OrdinalIgnoreCase));
                if (defaultProvider.name != null)
                    safetyModel = defaultProvider.model;
            }

            if (string.IsNullOrEmpty(safetyModel))
            {
                logger?.LogWarning("SafeChatClient: no model configured, skipping safety wrapper");
                return router;
            }

            var safetyKey = opts.AI.ApiKeyEnv != null ? LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv) ?? "" : "";
            if (string.IsNullOrEmpty(safetyKey))
            {
                logger?.LogWarning("SafeChatClient: no API key configured, skipping safety wrapper");
                return router;
            }

            IChatClient safetyClient = OpenAIChatClientFactory.Create(
                "https://api.deepseek.com/v1", safetyModel, safetyKey);

            var wrapped = new LTAI.Core.Safety.SafeChatClient(router, safetyClient, logger);
            // P1: wrap with MetricsChatClient for OTel metrics
            return new MetricsChatClient(wrapped, sp.GetService<ILogger<MetricsChatClient>>());
        });

        // Local ONNX embedder (BGE-small-zh, zero API dependency)        // Local ONNX embedder (BGE-small-zh, zero API dependency)
        // L0 默认使用本地 ONNX 模型，远程 embedding API 需通过 /model l0 手动切换。

        // P13.1 + P13.2: factory that reads LTAI:Embedding config at resolution
        // time and binds LocalEmbedder.Options before the ctor runs.
        // P14.9: also forward EmbeddingConfig.Models so ResolveModelFiles /
        // DownloadModelAsync can honor per-model quantization overrides.
        services.AddSingleton<LocalEmbedder>(sp =>
        {
            var embedOpts = sp.GetService<IOptions<LTAIOptions>>()?.Value.Embedding;
            if (embedOpts != null)
            {
                LocalEmbedder.Options = new EmbeddingOptions
                {
                    Gpu = embedOpts.Gpu,
                    Quantization = embedOpts.Quantization,
                    DeviceId = embedOpts.DeviceId,
                    Models = new Dictionary<string, string>(embedOpts.Models, StringComparer.OrdinalIgnoreCase),
                };
            }
            return new LocalEmbedder();
        });

        // Embedding client (API → local BGE → FastEmb fallback)
        // P14.4: auto-detect best execution provider (DML > CUDA > CPU) at startup
        services.AddHostedService<EpProbeService>();
        services.AddSingleton<EpProbeService>();

        services.AddSingleton<EmbeddingClient>(sp =>
            new EmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<LocalEmbedder>(),
                sp.GetService<ILogger<EmbeddingClient>>(),
                sp.GetService<RemoteEmbeddingCache>()));

        // P14.5: in-memory TTL cache for remote embedding API results.
        // Separate from ToolEmbeddingCache (which persists tool/agent
        // descriptions). Default 24h TTL bounds stale risk when remote
        // providers upgrade their models.
        services.AddSingleton<RemoteEmbeddingCache>(sp =>
            new RemoteEmbeddingCache(
                ttl: TimeSpan.FromHours(24),
                logger: sp.GetService<ILogger<RemoteEmbeddingCache>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteEmbeddingCache>.Instance));

        // P12: persistent embedding cache — 1 batched ONNX call per change-set,
        // JSON file under %LOCALAPPDATA%/LTAI/tool_embeddings.json, survives restarts.
        services.AddSingleton<ToolEmbeddingCache>(sp =>
            new ToolEmbeddingCache(
                sp.GetRequiredService<EmbeddingClient>(),
                sp.GetService<ILogger<ToolEmbeddingCache>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolEmbeddingCache>.Instance));

        // P14.12: opt-in background pre-warm of all known embedding models.
        // Gated by LTAI:Embedding:PreWarmAllModels (default false); no-ops when
        // remote API key is in use (DefaultDisabled) or no models directory.
        services.AddHostedService(sp => new PreWarmEmbeddingModelsHostedService(
            sp.GetRequiredService<IOptions<LTAIOptions>>(),
            sp.GetService<ILogger<PreWarmEmbeddingModelsHostedService>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PreWarmEmbeddingModelsHostedService>.Instance,
            sp.GetService<IHttpClientFactory>()));

        return services;
    }
}
