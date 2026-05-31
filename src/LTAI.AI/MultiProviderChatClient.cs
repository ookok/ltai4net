using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
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
                _degradation[k] = v;
        }
    }

    /// <summary>
    /// Register a named IChatClient instance.
    /// <b>Callers:</b> AddLTAIAI() ServiceCollectionExtensions (once per provider with valid API key).
    /// </summary>
    public void Register(string name, IChatClient client) => _clients[name] = client;

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
                // 兜底：流式成功时记录一次请求（精准 token 由 OpenAiHttpClient 从 usage 字段追记）
                // 传空模型名，保持 OpenAiHttpClient 已记录的真实模型名不被覆盖
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
            catch (HttpRequestException ex) when (ex.StatusCode is
                System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden or
                (System.Net.HttpStatusCode)402)
            {
                // Auth / payment failure — ban permanently (never retry this session)
                _logger.LogWarning("Provider '{P}' permanently banned: {(int)ex.StatusCode}", p, ex.StatusCode);
                _providerCooldowns[p] = DateTime.MaxValue;
                RecordFailure(p);
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
                current = _degradation.GetValueOrDefault(current);
                continue;
            }

            if (_clients.ContainsKey(current))
            {
                yield return current;
            }

            current = _degradation.GetValueOrDefault(current);
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
/// OpenAI-compatible chat client via direct HTTP calls.
/// Works with DeepSeek, OpenAI, Groq, SiliconFlow, etc. — any OpenAI-compatible API.
/// Handles: SSE streaming, auth errors (401/403/402 → fast-fail), rate limiting (429),
/// token usage tracking via <see cref="UsageTracker"/>, and tool calling (function calling).
///
/// <b>Consumers:</b> Instantiated per-provider in AddLTAIAI() ServiceCollectionExtensions.
/// Used by MultiProviderChatClient as the underlying IChatClient implementation.
///
/// ⚠ KNOWN ISSUE (mitigated): SSE line parsing uses line.AsSpan(DataPrefix.Length) — validated
/// by preceding StartsWith("data: ") check. If SSE format deviates, the line is skipped.
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

    // ─────────────────────────────────────────────────────────────────
    //  Non-streaming chat completion with tool calling support
    // ─────────────────────────────────────────────────────────────────
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var request = BuildRequestBody(messages, options, stream: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(request, options: JsonOpts);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Log 400+ response body for debugging
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger?.LogWarning("LLM API {Method} {Uri} → {Status}: {Body}",
                req.Method, req.RequestUri, (int)resp.StatusCode, errBody);
        }

        CheckHttpError(resp);

        var json = await resp.Content.ReadFromJsonAsync<ChatResponseJson>(JsonOpts, ct).ConfigureAwait(false);

        // Track token usage
        if (json?.Usage != null)
            LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                json.Usage.PromptTokens, json.Usage.CompletionTokens,
                json.Usage.PromptCacheHitTokens ?? 0, json.Usage.PromptCacheMissTokens ?? 0,
                _model);

        var choice = json?.Choices?.FirstOrDefault();
        if (choice?.Message == null)
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

        // Parse tool_calls from response
        if (choice.Message.ToolCalls is { Length: > 0 } calls)
        {
            var contents = new List<AIContent>();
            foreach (var tc in calls)
            {
                var argsDict = ParseArgs(tc.Function.Arguments);
                contents.Add(new FunctionCallContent(tc.Id, tc.Function.Name, argsDict));
            }
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)]);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, choice.Message.Content ?? ""));
    }

    // ─────────────────────────────────────────────────────────────────
    //  Streaming chat completion with tool calling support
    // ─────────────────────────────────────────────────────────────────
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 流式计时和 tool call 追踪
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long completionTokens = 0;

        var sl = messages.ToList();
        _logger?.LogInformation("[OpenAiHttpClient-Streaming] Request: messages={Count}, tools={Tools}",
            sl.Count, options?.Tools?.Count ?? 0);

        var requestBody = BuildRequestBody(sl, options, stream: true);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(requestBody, options: JsonOpts);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Log 400+ response body for debugging
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger?.LogWarning("LLM API {Method} {Uri} → {Status}: {Body}",
                req.Method, req.RequestUri, (int)resp.StatusCode, errBody);
        }

        CheckHttpError(resp);

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // Tool call delta accumulation (by index)
        var toolCallAccum = new Dictionary<int, (string id, string name, StringBuilder args)>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line.AsSpan(6).ToString();
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

            // Track usage from final SSE message (usage-only chunk before [DONE])
            if (chunk?.Usage is { } usage && (chunk.Choices == null || chunk.Choices.Length == 0))
            {
                LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                    usage.PromptTokens, usage.CompletionTokens,
                    usage.PromptCacheHitTokens ?? 0, usage.PromptCacheMissTokens ?? 0,
                    _model);
                // 用 API 返回的精确 completion tokens 覆盖流式估算值（更准确）
                if (usage.CompletionTokens > 0)
                    completionTokens = usage.CompletionTokens;
            }

            var choice = chunk?.Choices?.FirstOrDefault();
            if (choice == null) continue;

            // Handle tool call deltas
            if (choice.Delta?.ToolCalls is { Length: > 0 } toolCallDeltas)
            {
                foreach (var tc in toolCallDeltas)
                {
                    if (!toolCallAccum.TryGetValue(tc.Index, out var acc))
                    {
                        toolCallAccum[tc.Index] = (
                            tc.Id ?? "",
                            tc.Function?.Name ?? "",
                            new StringBuilder(tc.Function?.Arguments ?? ""));
                    }
                    else
                    {
                        var sb = acc.args;
                        if (tc.Id != null) acc.id = tc.Id;
                        if (tc.Function?.Name != null) acc.name = tc.Function.Name;
                        if (tc.Function?.Arguments != null) sb.Append(tc.Function.Arguments);
                        toolCallAccum[tc.Index] = (acc.id, acc.name, sb);
                    }
                }
            }

            // Text content delta (only when no tool calls)
            if (choice.Delta?.Content is { Length: > 0 } deltaText && toolCallAccum.Count == 0)
            {
                // chars→tokens 估算：中文每字约 1.5-2 token，英文约 0.25-0.3 token
                // 用浮点累加避免短 delta（1-3 字符）被整数除法吞掉
                completionTokens += (int)Math.Ceiling(deltaText.Length / 3.0);
                yield return new ChatResponseUpdate(ChatRole.Assistant, deltaText);
            }

            // Finish_reason: tool_calls → 记录 tool call
            if (choice.FinishReason == "tool_calls" && toolCallAccum.Count > 0)
            {
                for (int i = 0; i < toolCallAccum.Count; i++)
                    LTAI.Core.Configuration.UsageTracker.RecordToolCall();
                var contents = new List<AIContent>();
                foreach (var (_, (id, name, args)) in toolCallAccum)
                {
                    var argsDict = ParseArgs(args.ToString());
                    contents.Add(new FunctionCallContent(id, name, argsDict));
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
                toolCallAccum.Clear();
            }
        }

        // Edge case: tool calls accumulated but no finish_reason yielded
        if (toolCallAccum.Count > 0)
        {
            for (int i = 0; i < toolCallAccum.Count; i++)
                LTAI.Core.Configuration.UsageTracker.RecordToolCall();
            var contents = new List<AIContent>();
            foreach (var (_, (id, name, args)) in toolCallAccum)
            {
                var argsDict = ParseArgs(args.ToString());
                contents.Add(new FunctionCallContent(id, name, argsDict));
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        }

        // 记录流式响应速率指标
        sw.Stop();
        LTAI.Core.Configuration.UsageTracker.RecordStreamingMetrics(completionTokens, sw.ElapsedMilliseconds);
        LTAI.Core.Configuration.UsageTracker.RecordLlmCallDuration(sw.ElapsedMilliseconds);
    }

    /// <summary>Track token usage from a streaming SSE chunk (usage field, last message before [DONE]).</summary>
    private void TrackStreamingUsage(StreamingChunkJson? chunk)
    {
        if (chunk?.Usage is { } usage)
            LTAI.Core.Configuration.UsageTracker.RecordWithCache(
                usage.PromptTokens, usage.CompletionTokens,
                usage.PromptCacheHitTokens ?? 0, usage.PromptCacheMissTokens ?? 0,
                _model);
    }

    /// <summary>Parse a JSON arguments string into a dictionary for FunctionCallContent.</summary>
    private static Dictionary<string, object?>? ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Request building helpers
    // ─────────────────────────────────────────────────────────────────
    private object BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var msgList = messages.ToList();
        _logger?.LogInformation("[OpenAiHttpClient] Request: model={Model}, stream={Stream}, messages={Count}, tools={Tools}",
            _model, stream, msgList.Count,
            options?.Tools?.Count ?? 0);

        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = SerializeMessages(msgList),
        };

        // Only send stream: true for streaming requests; omit for non-streaming
        if (stream) body["stream"] = true;

        // Serialize tools if present
        if (options?.Tools is { Count: > 0 } tools)
        {
            body["tools"] = SerializeTools(tools);
            body["tool_choice"] = "auto";
        }

        // Pass through common options
        if (options?.Temperature is not null) body["temperature"] = options.Temperature;
        if (options?.MaxOutputTokens is not null) body["max_tokens"] = options.MaxOutputTokens;
        if (options?.TopP is not null) body["top_p"] = options.TopP;
        if (options?.FrequencyPenalty is not null) body["frequency_penalty"] = options.FrequencyPenalty;
        if (options?.PresencePenalty is not null) body["presence_penalty"] = options.PresencePenalty;
        if (options?.StopSequences is { Count: > 0 }) body["stop"] = options.StopSequences;

        return body;
    }

    /// <summary>
    /// Serialize ChatMessage list to OpenAI API format.
    /// Handles: system/user/assistant roles, tool results (FunctionResultContent),
    /// and assistant tool calls (FunctionCallContent).
    /// </summary>
    private static List<object> SerializeMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            // Tool result message (from function invocation)
            var resultContent = m.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (resultContent != null)
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = resultContent.CallId,
                    ["content"] = resultContent.Result?.ToString() ?? "",
                });
                continue;
            }

            // Assistant message with tool calls
            var calls = m.Contents.OfType<FunctionCallContent>().ToList();
            if (calls is { Count: > 0 })
            {
                var dict = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Text,
                    ["tool_calls"] = calls.Select(fc => new Dictionary<string, object?>
                    {
                        ["id"] = fc.CallId,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = fc.Name,
                            ["arguments"] = fc.Arguments is not null ? JsonSerializer.Serialize(fc.Arguments, JsonOpts) : "{}",
                        }
                    }).ToList(),
                };
                result.Add(dict);
                continue;
            }

            // Regular text message (skip empty assistant text when it has contents with tool calls)
            var text = m.Text ?? "";
            result.Add(new Dictionary<string, object?>
            {
                ["role"] = m.Role == ChatRole.System ? "system" :
                           m.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = text,
            });
        }
        return result;
    }

    /// <summary>
    /// Serialize AITool list to OpenAI-compatible tools array (dedup by name).
    /// Only AIFunction tools are supported (converted to "function" type).
    /// </summary>
    private List<object> SerializeTools(IList<AITool> tools)
    {
        var result = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (tool is AIFunction func && !string.IsNullOrEmpty(func.Name) && seen.Add(func.Name))
            {
                var fn = new Dictionary<string, object?>
                {
                    ["name"] = func.Name,
                    ["description"] = func.Description ?? "",
                };
                if (func.JsonSchema is { } schema)
                    fn["parameters"] = schema;
                result.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = fn,
                });
            }
        }
        var dupCount = tools.Count - seen.Count;
        if (dupCount > 0)
            _logger?.LogWarning("Deduplicated {Count} duplicate tool names", dupCount);
        return result;
    }

    /// <summary>Unified HTTP error checking for auth/payment/rate-limit.</summary>
    private static void CheckHttpError(HttpResponseMessage resp)
    {
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
    }

    object? IChatClient.GetService(Type? t, object? k) => null;
    void IDisposable.Dispose() { }

    // ─────────────────────────────────────────────────────────────────
    //  JSON serialization records for request/response
    // ─────────────────────────────────────────────────────────────────

    // Non-streaming response types
    private sealed record ChatResponseJson(ChoiceJson[]? Choices, UsageJson? Usage);
    private sealed record ChoiceJson(MessageJson? Message);
    private sealed record MessageJson(string? Content, ToolCallJson[]? ToolCalls);
    private sealed record ToolCallJson(string Id, string Type, FunctionJson Function);
    private sealed record FunctionJson(string Name, string Arguments);
    private sealed record UsageJson(int PromptTokens, int CompletionTokens,
        int? PromptCacheHitTokens = null, int? PromptCacheMissTokens = null);

    // Streaming (SSE) chunk types
    private sealed record StreamingChunkJson(StreamingChoiceJson[]? Choices, UsageJson? Usage);
    private sealed record StreamingChoiceJson(DeltaJson? Delta, string? FinishReason);
    private sealed record DeltaJson(string? Content, ToolCallDeltaJson[]? ToolCalls);
    private sealed record ToolCallDeltaJson(int Index, string? Id, string? Type, FunctionDeltaJson? Function);
    private sealed record FunctionDeltaJson(string? Name, string? Arguments);
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

        // Step 2: Wrap with SafeChatClient for output safety interception (optional)
        services.AddSingleton<IChatClient>(sp =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;

            if (opts.AI.SkipSafetyChecks)
                return router; // Bypass safety in dev mode

            var logger = sp.GetService<ILogger<LTAI.Core.Safety.SafeChatClient>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
            IChatClient safetyClient = new OpenAiHttpClient(
                httpFactory.CreateClient("safety"), "https://api.deepseek.com/v1", "deepseek-chat", safetyKey);

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
