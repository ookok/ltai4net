using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Manages LLM provider client instances, degradation chains, and ranked provider selection.
/// Owns the <c>_clients</c> dictionary and <c>_degradation</c> chain.
/// </summary>
public sealed class ProviderClientManager
{
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _degradation = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _edgeClients = new(StringComparer.OrdinalIgnoreCase);
    private string _defaultProvider = "";
    private readonly string _routingFallback = "l1";
    private readonly ModelMetadataProvider? _modelMetadata;
    private readonly CircuitBreakerManager _breaker;
    private readonly ILogger _logger;

    public ProviderClientManager(
        string defaultProvider,
        CircuitBreakerManager breaker,
        ModelMetadataProvider? modelMetadata = null,
        ILogger? logger = null,
        Dictionary<string, string>? degradationChain = null)
    {
        _defaultProvider = defaultProvider;
        _breaker = breaker;
        _modelMetadata = modelMetadata;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        if (degradationChain != null)
        {
            foreach (var (k, v) in degradationChain)
                _degradation.TryAdd(k, v);
        }
    }

    /// <summary>Names of all currently registered LLM clients.</summary>
    public IEnumerable<string> RegisteredProviders => _clients.Keys;

    /// <summary>Names of edge (local) registered clients.</summary>
    public IEnumerable<string> EdgeProviders => _edgeClients;

    /// <summary>Names of remote (API-key-based) registered clients.</summary>
    public IEnumerable<string> RemoteProviders => _clients.Keys.Where(k => !_edgeClients.Contains(k));

    public string? ActiveProvider
    {
        get => _defaultProvider == "" ? null : _defaultProvider;
        set => _defaultProvider = value ?? "";
    }

    /// <summary>Register or replace a named provider client.</summary>
    public void Register(string name, IChatClient client)
    {
        _clients[name] = client;
        _breaker.ClearProvider(name);
    }

    /// <summary>Register an edge (local inference) client. Edge providers are tried first in routing.</summary>
    public void RegisterEdge(string name, IChatClient client)
    {
        _clients[name] = client;
        _edgeClients.Add(name);
        _breaker.ClearProvider(name);
    }

    public bool HasDegradation(string name) => _degradation.ContainsKey(name);

    public void SetDegradation(string from, string to) => _degradation[from] = to;

    public IChatClient? GetClient(string name) =>
        _clients.TryGetValue(name, out var c) ? c : null;

    public IChatClient GetL3Client() =>
        _clients.TryGetValue("l3", out var c) ? c : _clients["l1"];

    public IChatClient GetL2Client() =>
        _clients.TryGetValue("l2", out var c) ? c : _clients["l1"];

    /// <summary>Ranked providers with edge-first priority.</summary>
    public IEnumerable<string> RankedProviders(string preferred)
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeCandidates = new List<(string Id, double Health)>();
        var remoteCandidates = new List<(string Id, double Health)>();
        var current = preferred;

        // Collect candidates along the degradation chain
        while (current != null && seen.Add(current))
        {
            if (_breaker.IsInCooldown(current))
            {
                _logger.LogDebug("Provider '{P}' in cooldown, skipping in degradation chain", current);
                current = _degradation.TryGetValue(current, out var next) ? next : null;
                continue;
            }

            if (_clients.ContainsKey(current))
            {
                var entry = (current, _breaker.CalcHealthScore(current));
                if (_edgeClients.Contains(current))
                    edgeCandidates.Add(entry);
                else
                    remoteCandidates.Add(entry);
            }

            current = _degradation.TryGetValue(current, out var next2) ? next2 : null;
        }

        // Sort edge candidates first, then remote — both by health score descending
        edgeCandidates.Sort((a, b) => b.Health.CompareTo(a.Health));
        remoteCandidates.Sort((a, b) => b.Health.CompareTo(a.Health));

        foreach (var e in edgeCandidates) yield return e.Id;
        foreach (var r in remoteCandidates) yield return r.Id;

        // Degradation chain exhausted — wide fallback via ModelMetadataProvider
        if (_modelMetadata != null)
        {
            foreach (var p in FallbackProviders(preferred, seen))
                yield return p;
        }
    }

    private IEnumerable<string> FallbackProviders(string preferred, HashSet<string> seen)
    {
        var recommended = _modelMetadata!.RecommendModel(
            ModelCapability.Chat | ModelCapability.Streaming, preferred);
        if (recommended != null && seen.Add(recommended.Value.Provider) &&
            _clients.ContainsKey(recommended.Value.Provider))
        {
            _logger.LogInformation("Fallback: recommending provider '{P}' model '{M}'",
                recommended.Value.Provider, recommended.Value.Model);
            var chain = recommended.Value.Provider;
            while (chain != null && seen.Add(chain))
            {
                if (_clients.ContainsKey(chain)) yield return chain;
                chain = _degradation.TryGetValue(chain, out var next) ? next : null;
            }
        }
    }

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
            if (_clients.ContainsKey(recommended.Provider))
                return recommended.Provider;
        }

        return _routingFallback;
    }

    public int EstimateContextTokens(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            if (!string.IsNullOrEmpty(m.Text))
                total += LTAI.Core.Configuration.TokenEstimator.Estimate(m.Text);
        }
        return total;
    }

    public void DisposeClients()
    {
        foreach (var c in _clients.Values) c.Dispose();
    }
}
