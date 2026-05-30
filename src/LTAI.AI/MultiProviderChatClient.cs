using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// Multi-LLM provider router with automatic degradation chain.
/// Auto-registers all providers with valid API keys at startup.
/// </summary>
public sealed class MultiProviderChatClient : IChatClient
{
    /// <summary>All known providers: (envVar, endpoint, model, displayName).
    /// Generated from <see cref="LTAI.Core.Configuration.KnownKeys.GetDefaultProviders"/> — single source of truth.</summary>
    public static readonly (string envVar, string endpoint, string model, string name)[] DefaultProviders =
        LTAI.Core.Configuration.KnownKeys.GetDefaultProviders();

    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MultiProviderChatClient> _logger;
    private string _defaultProvider;

    // Circuit breaker state per provider
    private readonly ConcurrentDictionary<string, int> _providerFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _providerCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailuresBeforeCooldown = 3;
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);

    // Response cache (LRU, 5min TTL)
    private static readonly MemoryCache _responseCache = new(new MemoryCacheOptions
    {
        SizeLimit = 256,
        ExpirationScanFrequency = TimeSpan.FromMinutes(1)
    });
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public IEnumerable<string> RegisteredProviders => _clients.Keys;
    public string ActiveProvider { get => _defaultProvider; set => _defaultProvider = value; }

    public MultiProviderChatClient(LTAIOptions options, ILogger<MultiProviderChatClient>? logger = null)
    {
        _defaultProvider = options.AI.DefaultProvider;
        _logger = logger ?? NullLogger<MultiProviderChatClient>.Instance;
        if (options.AI.DegradationChain != null)
        {
            foreach (var (k, v) in options.AI.DegradationChain)
                _degradation[k] = v;
        }
    }

    public void Register(string name, IChatClient client) => _clients[name] = client;

    public ChatClientMetadata? Metadata => new("MultiProvider", new Uri("https://github.com/ltai-org/ltai4net"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var provider = options?.ModelId ?? _defaultProvider;
        return await TryCallWithDegradation(provider, messages, options, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = options?.ModelId ?? _defaultProvider;
        bool anyAttempted = false;
        string? lastFailedProvider = null;
        foreach (var p in DegradationChain(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;
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
            if (success) yield break;
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
            var text = cached.Messages?.LastOrDefault()?.Text ?? "";
            LTAI.Core.Configuration.UsageTracker.Record(text.Length / 4, text.Length / 8, provider);
            return cached;
        }

        foreach (var p in DegradationChain(provider))
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
                // Add 15s per-provider timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                var result = await client.GetResponseAsync(messages, options, timeoutCts.Token)
                    .ConfigureAwait(false);

                // Store in cache (miss path)
                _responseCache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = CacheTtl
                });

                // Success — reset failure count
                _providerFailures.TryRemove(p, out _);
                _providerCooldowns.TryRemove(p, out _);

                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is
                System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden or
                (System.Net.HttpStatusCode)402)
            {
                // Auth / payment failure — ban permanently (never retry this session)
                _logger.LogWarning("Provider '{P}' permanently banned: {(int)ex.StatusCode}", p, ex.StatusCode);
                _providerCooldowns[p] = DateTime.MaxValue;
                continue;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited — use Retry-After header if available
                var cooldown = ex.Message.Contains("retry after:")
                    ? TimeSpan.FromSeconds(30)  // fallback
                    : TimeSpan.FromSeconds(30);
                _providerCooldowns[p] = DateTime.UtcNow + cooldown;
                _logger.LogWarning("Provider '{P}' rate limited, cooldown {Cooldown}s", p, cooldown.TotalSeconds);
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
        if (count >= MaxFailuresBeforeCooldown)
        {
            var until = DateTime.UtcNow + CooldownDuration;
            _providerCooldowns[provider] = until;
            _logger.LogWarning("Provider '{P}' failed {Count} times — cooling down until {Until}",
                provider, count, until);
        }
    }

    /// <summary>Build a deterministic cache key from provider, messages, and options.</summary>
    private static string BuildCacheKey(string provider, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(provider).Append('|');
        sb.Append(options?.Temperature ?? 0).Append('|');
        sb.Append(options?.MaxOutputTokens ?? 0).Append('|');
        foreach (var m in messages)
            sb.Append(m.Role).Append(':').Append(m.Text ?? "").Append('|');
        // Hash to keep key short
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }

    private IEnumerable<string> DegradationChain(string provider)
    {
        yield return provider;
        while (_degradation.TryGetValue(provider, out var fallback))
        {
            yield return fallback;
            provider = fallback;
        }
    }

    object? IChatClient.GetService(Type t, object? k) => t == typeof(ChatClientMetadata) ? Metadata : null;
    void IDisposable.Dispose() { foreach (var c in _clients.Values) c.Dispose(); }
}

/// <summary>
/// OpenAI-compatible chat client via direct HTTP calls.
/// Works with Deepseek, OpenAI, Groq, etc. — any OpenAI-compatible API.
/// </summary>
public sealed class OpenAiHttpClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public OpenAiHttpClient(HttpClient http, string endpoint, string model, string apiKey, ILogger? logger = null)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _logger = logger;
    }

    public ChatClientMetadata? Metadata => new("OpenAI-compat", new Uri(_endpoint));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var request = new
        {
            model = _model,
            messages = messages.Select(m => new
            {
                role = m.Role == ChatRole.System ? "system" :
                       m.Role == ChatRole.Assistant ? "assistant" : "user",
                content = m.Text ?? ""
            }).ToList()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(request, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        // Fast-fail on auth/payment errors — no point retrying these
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||      // 401
            resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||         // 403
            (int)resp.StatusCode == 402)                                     // Payment Required
        {
            throw new HttpRequestException($"Provider auth/payment failure ({(int)resp.StatusCode})", null, resp.StatusCode);
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)    // 429
        {
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            throw new HttpRequestException($"Rate limited, retry after: {retryAfter}", null, resp.StatusCode);
        }
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<ChatResponseJson>(JsonOpts, ct).ConfigureAwait(false);
        var text = json?.Choices?.FirstOrDefault()?.Message?.Content ?? "";

        // Track token usage
        if (json?.Usage != null)
            LTAI.Core.Configuration.UsageTracker.Record(json.Usage.PromptTokens, json.Usage.CompletionTokens, _model);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = _model,
            stream = true,
            messages = messages.Select(m => new
            {
                role = m.Role == ChatRole.System ? "system" :
                       m.Role == ChatRole.Assistant ? "assistant" : "user",
                content = m.Text ?? ""
            }).ToList()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(requestBody, options: JsonOpts);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Fast-fail on auth/payment/rate-limit (same as non-streaming)
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            (int)resp.StatusCode == 402)
        {
            throw new HttpRequestException($"Provider auth/payment failure ({(int)resp.StatusCode})", null, resp.StatusCode);
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            throw new HttpRequestException($"Rate limited, retry after: {retryAfter}", null, resp.StatusCode);
        }
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break; // end of stream
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            StreamingChunkJson? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamingChunkJson>(data, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "SSE parse error: {Data}", data);
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
            }
        }
    }

    object? IChatClient.GetService(Type? t, object? k) => null;
    void IDisposable.Dispose() { }

    private sealed record ChatResponseJson(ChoiceJson[]? Choices, UsageJson? Usage);
    private sealed record ChoiceJson(MessageJson? Message);
    private sealed record MessageJson(string? Content);
    private sealed record UsageJson(int PromptTokens, int CompletionTokens);

    // SSE streaming chunk types
    private sealed record StreamingChunkJson(StreamingChoiceJson[]? Choices);
    private sealed record StreamingChoiceJson(DeltaJson? Delta, string? FinishReason);
    private sealed record DeltaJson(string? Content);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        // Named HttpClient with connection pooling for LLM API calls
        // Reuses TCP+TLS connections to avoid ~200ms handshake overhead per request
        services.AddHttpClient("llm")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 3,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true,
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

                    if (isDefault)
                    {
                        // L1 (flash): from config deepseek-fast, fallback deepseek-v4-flash
                        var l1 = opts.AI.GetLayerConfig("fast");
                        var l1Ep = !string.IsNullOrEmpty(l1.Endpoint) ? l1.Endpoint : provider.endpoint;
                        var l1Http = httpFactory.CreateClient("llm");
                        var l1Client = new OpenAiHttpClient(l1Http, l1Ep, l1.Model, apiKey, logger as ILogger);
                        router.Register("deepseek", l1Client);

                        // L2 (pro): from config deepseek, fallback deepseek-v4-pro
                        var l2 = opts.AI.GetLayerConfig("pro");
                        var l2Ep = !string.IsNullOrEmpty(l2.Endpoint) ? l2.Endpoint : provider.endpoint;
                        var l2Http = httpFactory.CreateClient("llm");
                        var l2Client = new OpenAiHttpClient(l2Http, l2Ep, l2.Model, apiKey, logger as ILogger);
                        router.Register("deepseek-pro", l2Client);
                    }
                    else
                    {
                        var http = httpFactory.CreateClient("llm");
                        var client = new OpenAiHttpClient(http, provider.endpoint, provider.model, apiKey, logger as ILogger);
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

        // Step 2: Wrap with SafeChatClient for output safety interception
        services.AddSingleton<IChatClient>(sp =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var logger = sp.GetService<ILogger<LTAI.Core.Safety.SafeChatClient>>();

            // Build a safety LLM from the first available provider
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
            IChatClient safetyClient = new OpenAiHttpClient(
                httpFactory.CreateClient(), "https://api.deepseek.com/v1", "deepseek-chat", safetyKey);

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
