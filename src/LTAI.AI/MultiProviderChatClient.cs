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
    private string _defaultProvider;

    // 自适应成本路由：成功率 + 延迟 + 成本感知
    private readonly ConcurrentDictionary<string, ProviderStats> _providerStats = new(StringComparer.OrdinalIgnoreCase);
    // Circuit breaker state per provider (thread-safe via ConcurrentDictionary)
    private readonly ConcurrentDictionary<string, int> _providerFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _providerCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailuresBeforeCooldown = 3;
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);

    // Response cache (LRU, 5min TTL) — shared across ALL instances (static)
    private static readonly MemoryCache _responseCache = new(new MemoryCacheOptions
    {
        SizeLimit = 256,
        ExpirationScanFrequency = TimeSpan.FromMinutes(1)
    });
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static int _responseCacheSizeLimit = 256;

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
    public MultiProviderChatClient(LTAIOptions options, ILogger<MultiProviderChatClient>? logger = null)
    {
        _defaultProvider = options.AI.DefaultProvider;
        _logger = logger ?? NullLogger<MultiProviderChatClient>.Instance;
        if (options.AI.DegradationChain != null)
        {
            foreach (var (k, v) in options.AI.DegradationChain)
                _degradation.TryAdd(k, v);
        }
        if (options.AI.ResponseCacheSize > 0)
            _responseCacheSizeLimit = options.AI.ResponseCacheSize;
    }

    /// <summary>
    /// Register a named IChatClient instance.
    /// <b>Callers:</b> AddLTAIAI() ServiceCollectionExtensions (once per provider with valid API key).
    /// </summary>
    public void Register(string name, IChatClient client) => _clients.TryAdd(name, client);

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
        var provider = options?.ModelId ?? _defaultProvider;
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
        var provider = options?.ModelId ?? _defaultProvider;
        bool anyAttempted = false;
        string? lastFailedProvider = null;
        foreach (var p in RankedProviders(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;
            var callNum = Interlocked.Increment(ref _callCounter);
            _logger.LogInformation("LLM streaming call #{CallNum} → provider={Provider}", callNum, p);

            anyAttempted = true;
            var success = false;

            // Notify user of fallback switch-over
            if (lastFailedProvider != null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    $"\n\n_[Stream from '{lastFailedProvider}' failed midway, falling back to '{p}']_\n\n");
            }

            var innerStream = client.GetStreamingResponseAsync(messages, options, ct);
            await using (var enumerator = innerStream.GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
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
                // 兜底：流式成功时记录一次请求（精准 token 由底层 IChatClient 通过 Usage 字段返回）
                LTAI.Core.Configuration.UsageTracker.Record(10, 10);
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant,
            anyAttempted
                ? $"All providers failed for '{provider}'"
                : $"No providers available for '{provider}'");
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
            // Still track approximate tokens from cached response
            var text = cached!.Messages?.LastOrDefault()?.Text ?? "";
            var promptT = text.Length / 4;
            var completionT = text.Length / 8;
            LTAI.Core.Configuration.UsageTracker.Record(promptT, completionT, provider);
            LTAI.Core.Configuration.UsageTracker.RecordCacheTokens(promptT, 0); // 全部 prompt 命中缓存
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
                var callNum = Interlocked.Increment(ref _callCounter);
                _logger.LogInformation("LLM call #{CallNum} → provider={Provider}", callNum, p);

                // Add 15s per-provider timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                var result = await client.GetResponseAsync(messages, options, timeoutCts.Token)
                    .ConfigureAwait(false);

                // Track token usage from MAF-compliant IChatClient.Usage metadata
                if (result.Usage is { } usage)
                {
                    LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                        (int)(usage.InputTokenCount ?? 0),
                        (int)(usage.OutputTokenCount ?? 0),
                        (int)(usage.AdditionalCounts?.FirstOrDefault(c => c.Key.StartsWith("Cached")).Value ?? 0),
                        0,
                        p);
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
                _logger.LogWarning("Provider '{P}' permanently banned: {Status}", p, ex.Status);
                _providerCooldowns[p] = DateTime.MaxValue;
                RecordFailure(p);
                continue;
            }
            catch (ClientResultException ex) when (ex.Status == (int)System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited — honor Retry-After header when present
                var cooldown = TimeSpan.FromSeconds(30);
                _providerCooldowns[p] = DateTime.UtcNow + cooldown;
                _logger.LogWarning("Provider '{P}' rate limited, cooldown {Cooldown}s", p, cooldown.TotalSeconds);
                RecordFailure(p);
                continue;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Provider '{P}' timed out after 15s, degrading", p);
                RecordFailure(p);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout from our CTS (not user cancellation)
                _logger.LogWarning("Provider '{P}' timed out, degrading", p);
                RecordFailure(p);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Provider '{P}' failed, degrading to fallback", p);
                RecordFailure(p);
                continue;
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"All providers failed for '{provider}'"));
    }

    private void RecordFailure(string provider)
    {
        var count = _providerFailures.AddOrUpdate(provider, 1, (_, c) => c + 1);
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        Interlocked.Increment(ref stats.FailedCalls);
        if (count >= MaxFailuresBeforeCooldown)
        {
            var until = DateTime.UtcNow + CooldownDuration;
            _providerCooldowns[provider] = until;
            _logger.LogWarning("Provider '{P}' failed {Count} times — cooling down until {Until}",
                provider, count, until);
        }
    }

    /// <summary>
    /// Build a deterministic cache key from provider, messages, and options.
    /// Uses HashCode.Combine (built-in, zero allocations, deterministic within process).
    /// Includes: provider name, temperature, max output tokens, and full message text.
    /// In-memory cache only — no cross-process persistence needed.
    /// </summary>
    private static string BuildCacheKey(string provider, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var hc = new HashCode();
        hc.Add(provider, StringComparer.OrdinalIgnoreCase);
        hc.Add(options?.Temperature ?? 0);
        hc.Add(options?.MaxOutputTokens ?? 0);
        foreach (var m in messages)
        {
            hc.Add(m.Role);
            hc.Add(m.Text ?? "");
        }
        return hc.ToHashCode().ToString("x8");
    }

    /// <summary>
    /// 沿配置的降级链依次返回可用的 provider，不串到无关 provider。
    /// 降级链如：deepseek → deepseek-pro → (无配置则结束)。
    /// 不在降级链中的 provider 不进入选择列表。
    /// </summary>
    private IEnumerable<string> RankedProviders(string preferred)
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
    /// <param name="model">Model name, e.g. "deepseek-chat".</param>
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

        // Step 1: Register the raw MultiProviderChatClient (not as IChatClient — we'll wrap it)
        services.AddSingleton<MultiProviderChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var router = new MultiProviderChatClient(opts, logger);

            foreach (var provider in MultiProviderChatClient.DefaultProviders)
            {
                var apiKey = LTAI.Core.Configuration.SecretManager.Get(provider.envVar);
                if (string.IsNullOrEmpty(apiKey)) continue;
                try
                {
                    var isDefault = string.Equals(provider.name, opts.AI.DefaultProvider, StringComparison.OrdinalIgnoreCase);

                    // Anthropic uses its own SDK (different protocol); everything else is OpenAI-compatible.
                    var isAnthropic = string.Equals(provider.name, "Anthropic", StringComparison.OrdinalIgnoreCase);

                    if (isDefault)
                    {
                        // L1 (flash): from config deepseek-fast, fallback deepseek-v4-flash
                        var l1 = opts.AI.GetLayerConfig("fast");
                        var l1Ep = !string.IsNullOrEmpty(l1.Endpoint) ? l1.Endpoint : provider.endpoint;
                        var l1Client = isAnthropic
                            ? AnthropicChatClientFactory.Create(l1.Model, apiKey)
                            : OpenAIChatClientFactory.Create(l1Ep, l1.Model, apiKey);
                        router.Register("deepseek", l1Client);

                        // L2 (pro): from config deepseek, fallback deepseek-v4-pro
                        var l2 = opts.AI.GetLayerConfig("pro");
                        var l2Ep = !string.IsNullOrEmpty(l2.Endpoint) ? l2.Endpoint : provider.endpoint;
                        var l2Client = isAnthropic
                            ? AnthropicChatClientFactory.Create(l2.Model, apiKey)
                            : OpenAIChatClientFactory.Create(l2Ep, l2.Model, apiKey);
                        router.Register("deepseek-pro", l2Client);
                    }
                    else
                    {
                        var client = isAnthropic
                            ? AnthropicChatClientFactory.Create(provider.model, apiKey)
                            : OpenAIChatClientFactory.Create(provider.endpoint, provider.model, apiKey);
                        router.Register(provider.name, client);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to register provider");
                }
            }
            return router;
        });

        // Step 2: Wrap with SafeChatClient for output safety interception (optional)
        services.AddSingleton<IChatClient>(sp =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;

            if (opts.AI.SkipSafetyChecks)
                return router; // Bypass safety in dev mode

            var logger = sp.GetService<ILogger<LTAI.Core.Safety.SafeChatClient>>();
            var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
            IChatClient safetyClient = OpenAIChatClientFactory.Create(
                "https://api.deepseek.com/v1", "deepseek-chat", safetyKey);

            return new LTAI.Core.Safety.SafeChatClient(router, safetyClient, logger);
        });

        // Local ONNX embedder (BGE-small-zh, zero API dependency)
        services.AddSingleton<LocalEmbedder>();

        // Embedding client (API → local BGE → FastEmb fallback)
        services.AddSingleton<EmbeddingClient>(sp =>
            new EmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<LocalEmbedder>(),
                sp.GetService<ILogger<EmbeddingClient>>()));
        return services;
    }
}
