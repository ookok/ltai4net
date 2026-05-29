using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
    /// <summary>All known providers: (envVar, endpoint, model, displayName).</summary>
    public static readonly (string envVar, string endpoint, string model, string name)[] DefaultProviders =
    {
        ("DEEPSEEK_API_KEY",     "https://api.deepseek.com/v1",              "deepseek-chat",                "DeepSeek"),
        ("SILICONFLOW_API_KEY",  "https://api.siliconflow.cn/v1",           "deepseek-ai/DeepSeek-V2.5",    "SiliconFlow"),
        ("DASHSCOPE_API_KEY",    "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",          "Aliyun"),
        ("ZHIPU_API_KEY",        "https://open.bigmodel.cn/api/paas/v4",    "glm-4-plus",                   "Zhipu"),
        ("DOUBAO_API_KEY",       "https://ark.cn-beijing.volces.com/api/v3","ep-XXXXXX",                    "Doubao"),
        ("HUNYUAN_API_KEY",      "https://api.hunyuan.cloud.tencent.com/v1","hunyuan-pro",                  "Hunyuan"),
        ("BAIDU_API_KEY",        "https://aip.baidubce.com/rpc/2.0/ai_custom", "ernie-4.0",                "Baidu"),
        ("SPARK_API_KEY",        "https://spark-api.xf-yun.com/v3.5/chat",  "spark-3.5",                    "iFlytek"),
        ("MOONSHOT_API_KEY",     "https://api.moonshot.cn/v1",              "moonshot-v1-8k",               "Moonshot"),
        ("BAICHUAN_API_KEY",     "https://api.baichuan-ai.com/v1",          "Baichuan4",                    "Baichuan"),
        ("YI_API_KEY",           "https://api.lingyiwanwu.com/v1",          "yi-large",                     "Yi"),
        ("STEP_API_KEY",         "https://api.stepfun.com/v1",              "step-2-16k",                   "StepFun"),
        ("MINIMAX_API_KEY",      "https://api.minimax.chat/v1",             "MiniMax-Text-01",              "Minimax"),
        ("OPENAI_API_KEY",       "https://api.openai.com/v1",               "gpt-4o",                       "OpenAI"),
        ("GROQ_API_KEY",         "https://api.groq.com/openai/v1",          "llama-3.3-70b-versatile",      "Groq"),
        ("OPENROUTER_API_KEY",   "https://openrouter.ai/api/v1",            "deepseek/deepseek-chat",       "OpenRouter"),
        ("TOGETHER_API_KEY",     "https://api.together.xyz/v1",             "mistralai/Mixtral-8x22B",      "TogetherAI"),
        ("MISTRAL_API_KEY",      "https://api.mistral.ai/v1",               "mistral-large-latest",         "Mistral"),
        ("PERPLEXITY_API_KEY",   "https://api.perplexity.ai",               "sonar-pro",                    "Perplexity"),
        ("XAI_API_KEY",          "https://api.x.ai/v1",                     "grok-2-1212",                  "XAI"),
        ("COHERE_API_KEY",       "https://api.cohere.ai/v1",                "command-r-plus",               "Cohere"),
        ("FIREWORKS_API_KEY",    "https://api.fireworks.ai/inference/v1",   "accounts/fireworks/models/llama-v3p3-70b-instruct", "Fireworks"),
    };

    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MultiProviderChatClient> _logger;
    private string _defaultProvider;

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
        foreach (var p in DegradationChain(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;
            anyAttempted = true;
            var success = false;
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
        foreach (var p in DegradationChain(provider))
        {
            if (!_clients.TryGetValue(p, out var client)) continue;
            try
            {
                return await client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider '{P}' failed, degrading to fallback", p);
                continue;
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"All providers failed for '{provider}'"));
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
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<ChatResponseJson>(JsonOpts, ct).ConfigureAwait(false);
        var text = json?.Choices?.FirstOrDefault()?.Message?.Content ?? "";

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

    private sealed record ChatResponseJson(ChoiceJson[]? Choices);
    private sealed record ChoiceJson(MessageJson? Message);
    private sealed record MessageJson(string? Content);

    // SSE streaming chunk types
    private sealed record StreamingChunkJson(StreamingChoiceJson[]? Choices);
    private sealed record StreamingChoiceJson(DeltaJson? Delta, string? FinishReason);
    private sealed record DeltaJson(string? Content);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<IChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var router = new MultiProviderChatClient(opts, logger);

            foreach (var provider in MultiProviderChatClient.DefaultProviders)
            {
                var apiKey = Environment.GetEnvironmentVariable(provider.envVar);
                if (string.IsNullOrEmpty(apiKey)) continue;
                try
                {
                    var client = new OpenAiHttpClient(httpFactory.CreateClient(), provider.endpoint, provider.model, apiKey, logger as ILogger);
                    router.Register(provider.name, client);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to register provider");
                }
            }
            return router;
        });

        services.AddSingleton(sp => (MultiProviderChatClient)sp.GetRequiredService<IChatClient>());

        // Embedding client (API-based, no local model)
        services.AddSingleton<EmbeddingClient>(sp =>
            new EmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<ILogger<EmbeddingClient>>()));
        return services;
    }
}
