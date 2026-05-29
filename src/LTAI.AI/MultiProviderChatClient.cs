using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// Multi-LLM provider router with automatic degradation chain.
/// Loads degradation_chain from appsettings.Pricing.json via LTAIOptions.
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
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            return new MultiProviderChatClient(options.Value, logger);
        });
        return services;
    }
}
