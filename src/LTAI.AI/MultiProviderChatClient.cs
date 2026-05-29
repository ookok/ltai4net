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
/// Registers providers externally via Register().
/// </summary>
public sealed class MultiProviderChatClient : IChatClient
{
    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MultiProviderChatClient> _logger;
    private readonly string _defaultProvider;

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
        foreach (var p in DegradationChain(provider))
        {
            if (_clients.TryGetValue(p, out var client))
            {
                await foreach (var u in client.GetStreamingResponseAsync(messages, options, ct))
                    yield return u;
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, $"All providers failed for '{provider}'");
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
internal sealed class OpenAiHttpClient : IChatClient
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
        var response = await GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages?.LastOrDefault()?.Text ?? "");
    }

    object? IChatClient.GetService(Type? t, object? k) => null;
    void IDisposable.Dispose() { }

    private sealed record ChatResponseJson(ChoiceJson[]? Choices);
    private sealed record ChoiceJson(MessageJson? Message);
    private sealed record MessageJson(string? Content);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<BudgetTracker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new BudgetTracker(opts.AI.GlobalTokenBudget, opts.AI.PerUserTokenBudget);
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var router = new MultiProviderChatClient(opts, logger);

            // Register default provider via OpenAI-compatible HTTP
            var apiKey = Environment.GetEnvironmentVariable(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY");
            if (!string.IsNullOrEmpty(apiKey))
            {
                var endpoint = opts.AI.Providers.GetValueOrDefault(opts.AI.DefaultProvider)?.Endpoint
                    ?? "https://api.deepseek.com/v1";
                var model = opts.AI.Model;

                var client = new OpenAiHttpClient(http, endpoint, model, apiKey, logger as ILogger);
                router.Register(opts.AI.DefaultProvider, client);
                logger?.LogInformation("Registered '{P}' → {E} model={M}",
                    opts.AI.DefaultProvider, endpoint, model);
            }
            else
            {
                logger?.LogWarning("No API key for {Key}, LLM will fail",
                    opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY");
            }

            return router;
        });

        return services;
    }
}
