using System.Runtime.CompilerServices;
using System.Text;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Providers;

public sealed class MultiProviderChatClient : IChatClient
{
    private readonly Dictionary<string, IChatClient> _providerClients;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<MultiProviderChatClient> _logger;
    private readonly BudgetTracker _budget;

    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil;
    private const int MaxRetries = 3;
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(30);

    public MultiProviderChatClient(
        IEnumerable<KeyValuePair<string, IChatClient>> providerClients,
        IOptions<LTAIOptions> options,
        ILogger<MultiProviderChatClient> logger,
        BudgetTracker budget)
    {
        _providerClients = new Dictionary<string, IChatClient>(providerClients);
        _options = options;
        _logger = logger;
        _budget = budget;
    }

    public ChatClientMetadata? Metadata
    {
        get
        {
            var ai = _options.Value.AI;
            return new ChatClientMetadata(ai.L2.Model);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _budget.CheckBudget();

        if (DateTime.UtcNow < _circuitOpenUntil)
            throw new InvalidOperationException($"Circuit breaker open until {_circuitOpenUntil:O}");

        var modelKey = options?.ModelId ?? _options.Value.AI.L2.Model;
        var (client, config) = ResolveClient(modelKey);

        var chatOptions = BuildOptions(options, config, modelKey);

        Exception? lastEx = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await client.GetResponseAsync(messages, chatOptions, cancellationToken);
                Interlocked.Exchange(ref _consecutiveFailures, 0);

                if (response.Usage != null)
                    _budget.EstimateCost((int)(response.Usage.TotalTokenCount ?? 0), modelKey);

                return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitBreakerThreshold)
                {
                    _circuitOpenUntil = DateTime.UtcNow + CircuitCooldown;
                    _logger.LogError("Circuit breaker OPEN after {Failures} failures", failures);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    throw new InvalidOperationException($"Circuit breaker open: {ex.Message}", ex);
                }
                if (attempt < MaxRetries)
                {
                    var delayMs = 200 * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning("Retry {A}/{M} on provider {Provider}: {Error}", attempt, MaxRetries, config.Name, ex.Message);
                    await Task.Delay(delayMs, cancellationToken);

                    var degradedModel = GetDegradedModel(modelKey);
                    if (degradedModel != modelKey)
                    {
                        modelKey = degradedModel;
                        (client, config) = ResolveClient(modelKey);
                        chatOptions = BuildOptions(options, config, modelKey);
                        _logger.LogInformation("Degraded to model {Model} on provider {Provider}", modelKey, config.Name);
                    }
                }
            }
        }

        throw lastEx!;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _budget.CheckBudget();

        var modelKey = options?.ModelId ?? _options.Value.AI.L2.Model;
        var (client, config) = ResolveClient(modelKey);

        var chatOptions = BuildOptions(options, config, modelKey);

        await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
            yield return update;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    private (IChatClient Client, ProviderConfig Config) ResolveClient(string modelKey)
    {
        var aiConfig = _options.Value.AI;

        string? providerName = null;
        ProviderConfig? config = null;

        foreach (var kv in aiConfig.Providers)
        {
            if (string.Equals(kv.Value.Model, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                providerName = kv.Key;
                config = kv.Value;
                break;
            }
        }

        if (providerName == null && aiConfig.Providers.TryGetValue(modelKey, out config))
            providerName = modelKey;

        if (providerName == null)
        {
            providerName = aiConfig.DefaultProvider;
            config = aiConfig.Providers.GetValueOrDefault(providerName);
        }

        if (providerName == null)
        {
            var firstKv = _providerClients.FirstOrDefault();
            if (firstKv.Value == null)
                throw new InvalidOperationException($"No provider client registered. Requested model key: {modelKey}");
            providerName = firstKv.Key;
            config = aiConfig.Providers.Values.FirstOrDefault()
                ?? new ProviderConfig { Endpoint = "", ApiKey = "", Model = modelKey, Name = "default" };
        }

        if (!_providerClients.TryGetValue(providerName, out var client))
        {
            var entry = _providerClients.FirstOrDefault();
            if (entry.Value == null)
                throw new InvalidOperationException($"No provider client registered for '{providerName}'");
            client = entry.Value;
        }

        return (client, config ?? new ProviderConfig { Endpoint = "", ApiKey = "", Model = modelKey, Name = providerName });
    }

    private ChatOptions BuildOptions(ChatOptions? options, ProviderConfig config, string modelKey)
    {
        var aiConfig = _options.Value.AI;
        return new ChatOptions
        {
            ModelId = modelKey,
            Temperature = options?.Temperature ?? aiConfig.DefaultTemperature,
            MaxOutputTokens = options?.MaxOutputTokens ?? aiConfig.MaxTokens,
            Tools = options?.Tools,
            AdditionalProperties = options?.AdditionalProperties
        };
    }

    private string GetDegradedModel(string model)
    {
        var chain = _options.Value.ModelPricing.DegradationChain;
        if (chain.TryGetValue(model, out var fallback))
            return fallback;
        return _options.Value.AI.L1.Model;
    }

    void IDisposable.Dispose()
    {
        foreach (var client in _providerClients.Values)
            (client as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
