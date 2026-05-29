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
    private readonly PrefixCacheStore _prefixCache;

    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil;
    private const int MaxRetries = 3;
    private const int CircuitBreakerThreshold = 5;
    private const int ToolResultCapTokens = 3000;
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(30);

    public MultiProviderChatClient(
        IEnumerable<KeyValuePair<string, IChatClient>> providerClients,
        IOptions<LTAIOptions> options,
        ILogger<MultiProviderChatClient> logger,
        BudgetTracker budget,
        PrefixCacheStore prefixCache)
    {
        _providerClients = new Dictionary<string, IChatClient>(providerClients);
        _options = options;
        _logger = logger;
        _budget = budget;
        _prefixCache = prefixCache;
    }

    public ChatClientMetadata? Metadata
    {
        get
        {
            var ai = _options.Value.AI;
            return new ChatClientMetadata(ai.GetLayerConfig("deep").Model);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _budget.CheckBudget();

        if (DateTime.UtcNow < _circuitOpenUntil)
            throw new InvalidOperationException($"Circuit breaker open until {_circuitOpenUntil:O}");

        var modelKey = options?.ModelId ?? _options.Value.AI.GetLayerConfig("deep").Model;
        var (client, config) = ResolveClient(modelKey);

        var chatOptions = BuildOptions(options, config, modelKey);

        Exception? lastEx = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await client.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
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
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

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

        var ai = _options.Value.AI;
        var modelKey = options?.ModelId ?? ai.GetLayerConfig("deep").Model;

        if (_providerClients.Count == 0)
        {
            var whichLayer = modelKey == ai.GetLayerConfig("deep").Model ? "Deep" :
                             modelKey == ai.GetLayerConfig("fast").Model ? "Fast" :
                             modelKey == ai.GetLayerConfig("embedding").Model ? "Embedding" : modelKey;
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"[Model Not Configured] {whichLayer} 层的模型未配置。\n\n请在 Settings → LLM Config 中为此层设置 Provider 和 Model，并确保已填写 API Key。");
            yield break;
        }

        var useFlashFirst = modelKey == ai.GetLayerConfig("deep").Model && !string.IsNullOrEmpty(ai.GetLayerConfig("fast").Model);
        if (useFlashFirst)
        {
            var (flashClient, flashConfig) = ResolveClient(ai.GetLayerConfig("fast").Model);
            var flashOptions = BuildOptions(options, flashConfig, ai.GetLayerConfig("fast").Model);

            var flashBuf = new StringBuilder();
            var needsPro = false;
            var flashResults = new List<ChatResponseUpdate>();

            var msgList = messages as IList<ChatMessage>;
            var msgCount = msgList?.Count ?? 0;

            try
            {
                await foreach (var update in flashClient.GetStreamingResponseAsync(messages, flashOptions, cancellationToken))
                {
                    if (!needsPro && update.Text != null)
                    {
                        flashBuf.Append(update.Text);
                        if (flashBuf.Length > 0)
                        {
                            CheckNeedsPro(flashBuf, out needsPro);
                            if (needsPro)
                            {
                                _logger.LogInformation("Flash model self-reported NEEDS_PRO, escalating to {Pro}", ai.GetLayerConfig("deep").Model);
                                flashResults.Add(new ChatResponseUpdate(ChatRole.Assistant, "\n[escalating to " + ai.GetLayerConfig("deep").Model + "]\n"));
                                break;
                            }
                        }
                    }
                    flashResults.Add(update);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flash model failed for {Model}, falling back to Pro {Pro}", ai.GetLayerConfig("fast").Model, ai.GetLayerConfig("deep").Model);
                if (msgList != null && msgList.Count > msgCount)
                {
                    var removed = msgList.Count - msgCount;
                    while (msgList.Count > msgCount)
                        msgList.RemoveAt(msgList.Count - 1);
                    _logger.LogInformation("Removed {Count} orphaned messages from Flash pipeline failure", removed);
                }
            }

            foreach (var update in flashResults)
                yield return update;

            if (!needsPro) yield break;
        }

        var (client, config) = ResolveClient(modelKey);
        var chatOptions = BuildOptions(options, config, modelKey);

        await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
            yield return update;
    }

    private static void CheckNeedsPro(StringBuilder buf, out bool needsPro)
    {
        needsPro = false;
        var text = buf.ToString();
        if (text.StartsWith("<<<NEEDS_PRO>>>", StringComparison.Ordinal))
        {
            needsPro = true;
            return;
        }

        if (text.Contains("<<<NEEDS_PRO>>>", StringComparison.Ordinal))
        {
            needsPro = true;
            return;
        }
    }

    public static string CapToolResult(string result) =>
        ToolCallRepairer.CapToolResult(result, ToolResultCapTokens);

    public static void ClearStormHistory(string sessionId = "")
    {
        ToolCallRepairer.ClearStormHistory(sessionId);
    }

    public PrefixCacheStore PrefixCache => _prefixCache;

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata :
        serviceType == typeof(PrefixCacheStore) ? _prefixCache : null;

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
            providerName = aiConfig.Provider;
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

        if (!_providerClients.TryGetValue(providerName, out var newClient))
        {
            var entry = _providerClients.FirstOrDefault();
            if (entry.Value == null)
                throw new InvalidOperationException($"No provider client registered for '{providerName}'");
            newClient = entry.Value;
        }

        return (newClient, config ?? new ProviderConfig { Endpoint = "", ApiKey = "", Model = modelKey, Name = providerName });
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
        return _options.Value.AI.GetLayerConfig("fast").Model;
    }

    void IDisposable.Dispose()
    {
        foreach (var client in _providerClients.Values)
            (client as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
