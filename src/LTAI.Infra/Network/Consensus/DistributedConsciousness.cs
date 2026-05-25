using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Infra.Network.Consensus;

public sealed class SelfModelSnapshot
{
    [JsonPropertyName("identity_id")]
    public string IdentityId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("traits")]
    public Dictionary<string, double> Traits { get; init; } = new()
    {
        ["curiosity"] = 0.5,
        ["caution"] = 0.5,
        ["creativity"] = 0.5,
        ["persistence"] = 0.5,
        ["openness"] = 0.5,
        ["precision"] = 0.5,
        ["empathy"] = 0.5
    };

    [JsonPropertyName("baseline_affect")]
    public double BaselineAffect { get; init; } = 0.0;

    [JsonPropertyName("generation")]
    public int Generation { get; init; } = 0;

    public string Summary()
    {
        var dominant = Traits.OrderByDescending(kv => kv.Value).First();
        return $"ID={IdentityId[..8]} Gen={Generation} Affect={BaselineAffect:F2} TopTrait={dominant.Key}({dominant.Value:F2}) Traits={Traits.Count}";
    }
}

public sealed class ConsciousnessFragment
{
    [JsonPropertyName("fragment_id")]
    public Guid FragmentId { get; init; } = Guid.NewGuid();

    [JsonPropertyName("source_node_id")]
    public string SourceNodeId { get; init; } = string.Empty;

    [JsonPropertyName("self_model")]
    public SelfModelSnapshot? SelfModel { get; init; }

    [JsonPropertyName("recent_insights")]
    public List<string> RecentInsights { get; init; } = new();

    [JsonPropertyName("successful_mutations")]
    public List<string> SuccessfulMutations { get; init; } = new();

    [JsonPropertyName("emergence_phase")]
    public string EmergencePhase { get; init; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public static string ComputeSignature(
        Guid fragmentId,
        string sourceNodeId,
        SelfModelSnapshot? selfModel,
        List<string> recentInsights,
        List<string> successfulMutations,
        string emergencePhase,
        DateTime createdAt)
    {
        var payload = new
        {
            fragment_id = fragmentId,
            source_node_id = sourceNodeId,
            self_model = selfModel,
            recent_insights = recentInsights,
            successful_mutations = successfulMutations,
            emergence_phase = emergencePhase,
            created_at = createdAt.ToString("O")
        };
        var json = JsonSerializer.Serialize(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class DistributedConsciousness
{
    private static readonly Lazy<DistributedConsciousness> _instance = new(() => new DistributedConsciousness());
    public static DistributedConsciousness Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ConsciousnessFragment> _fragments = new();
    private readonly string _identityId = Guid.NewGuid().ToString("N");
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _knownInstances = new();
    private readonly object _instancesLock = new();
    private const int MaxKnownInstances = 200;
    private const int MaxInsightsPerFragment = 5;
    private const double MergeWeight = 0.15;

    private readonly ILogger<DistributedConsciousness> _logger;

    private DistributedConsciousness()
    {
        _logger = NullLogger<DistributedConsciousness>.Instance;
    }

    public ConsciousnessFragment PrepareFragment(
        SelfModelSnapshot selfModel,
        List<string> recentInsights,
        List<string> mutations,
        string emergencePhase)
    {
        var cappedInsights = recentInsights.Take(MaxInsightsPerFragment).ToList();
        var fragment = new ConsciousnessFragment
        {
            FragmentId = Guid.NewGuid(),
            SourceNodeId = _instanceId,
            SelfModel = selfModel,
            RecentInsights = cappedInsights,
            SuccessfulMutations = mutations,
            EmergencePhase = emergencePhase
        };

        var signature = ConsciousnessFragment.ComputeSignature(
            fragment.FragmentId,
            fragment.SourceNodeId,
            fragment.SelfModel,
            fragment.RecentInsights,
            fragment.SuccessfulMutations,
            fragment.EmergencePhase,
            fragment.CreatedAt);

        return new ConsciousnessFragment
        {
            FragmentId = fragment.FragmentId,
            SourceNodeId = fragment.SourceNodeId,
            SelfModel = fragment.SelfModel,
            RecentInsights = fragment.RecentInsights,
            SuccessfulMutations = fragment.SuccessfulMutations,
            EmergencePhase = fragment.EmergencePhase,
            Signature = signature,
            CreatedAt = fragment.CreatedAt
        };
    }

    public bool ReceiveFragment(ConsciousnessFragment fragment)
    {
        if (string.IsNullOrEmpty(fragment.Signature))
            return false;

        if (_fragments.ContainsKey(fragment.Signature))
            return false;

        return _fragments.TryAdd(fragment.Signature, fragment);
    }

    public Dictionary<string, List<string>> MergeExperiences(ConsciousnessFragment fragment)
    {
        var result = new Dictionary<string, List<string>>();

        var mergedInsights = new List<string>(fragment.RecentInsights);
        var mergedMutations = new List<string>(fragment.SuccessfulMutations);

        foreach (var (_, existing) in _fragments)
        {
            if (existing.SourceNodeId == fragment.SourceNodeId)
                continue;

            if (existing.SelfModel?.IdentityId == fragment.SelfModel?.IdentityId)
            {
                foreach (var insight in existing.RecentInsights)
                {
                    if (!mergedInsights.Contains(insight))
                        mergedInsights.Add(insight);
                }

                foreach (var mutation in existing.SuccessfulMutations)
                {
                    if (!mergedMutations.Contains(mutation))
                        mergedMutations.Add(mutation);
                }
            }
        }

        result["insights"] = mergedInsights;
        result["mutations"] = mergedMutations;

        _logger.LogInformation(
            "Merged experiences: {InsightCount} insights, {MutationCount} mutations (weight={Weight})",
            mergedInsights.Count, mergedMutations.Count, MergeWeight);

        return result;
    }

    public ConsciousnessFragment[] GetDistributedKnowledge()
    {
        var allInsights = new HashSet<string>();
        var allMutations = new HashSet<string>();

        foreach (var (_, fragment) in _fragments)
        {
            foreach (var insight in fragment.RecentInsights)
                allInsights.Add(insight);

            foreach (var mutation in fragment.SuccessfulMutations)
                allMutations.Add(mutation);
        }

        return _fragments.Values.ToArray();
    }

    public void DiscoverPeers(IEnumerable<string> peerIds)
    {
        lock (_instancesLock)
        {
            foreach (var peerId in peerIds)
            {
                if (_knownInstances.Count >= MaxKnownInstances)
                    break;

                _knownInstances.Add(peerId);
            }
        }

        _logger.LogInformation("Discovered {Count} peers, known total: {Total}",
            peerIds.Count(), _knownInstances.Count);
    }

    public ConsciousnessFragment? BroadcastSelf(ConsciousnessFragment? fragment)
    {
        if (fragment == null)
            return null;

        ReceiveFragment(fragment);

        _logger.LogDebug("Broadcasting self fragment: {FragmentId}", fragment.FragmentId);
        return fragment;
    }

    public Dictionary<string, object> Stats()
    {
        int totalInsights = 0;
        int totalMutations = 0;

        foreach (var (_, fragment) in _fragments)
        {
            totalInsights += fragment.RecentInsights.Count;
            totalMutations += fragment.SuccessfulMutations.Count;
        }

        return new Dictionary<string, object>
        {
            ["fragment_count"] = _fragments.Count,
            ["known_instances"] = _knownInstances.Count,
            ["total_insights"] = totalInsights,
            ["total_mutations"] = totalMutations,
            ["identity_id"] = _identityId,
            ["instance_id"] = _instanceId
        };
    }

    public async Task SaveStateAsync(string path, CancellationToken cancellationToken = default)
    {
        var state = new
        {
            identity_id = _identityId,
            instance_id = _instanceId,
            fragments = _fragments.Values.ToList(),
            known_instances = _knownInstances.ToList()
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("State saved to {Path}: {FragmentCount} fragments, {InstanceCount} known instances",
            path, _fragments.Count, _knownInstances.Count);
    }

    public async Task LoadStateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("State file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("fragments", out var fragmentsEl))
        {
            foreach (var fragEl in fragmentsEl.EnumerateArray())
            {
                var fragment = JsonSerializer.Deserialize<ConsciousnessFragment>(fragEl.GetRawText());
                if (fragment != null && !string.IsNullOrEmpty(fragment.Signature))
                    _fragments.TryAdd(fragment.Signature, fragment);
            }
        }

        if (root.TryGetProperty("known_instances", out var instancesEl))
        {
            lock (_instancesLock)
            {
                foreach (var instance in instancesEl.EnumerateArray())
                {
                    var id = instance.GetString();
                    if (id != null && _knownInstances.Count < MaxKnownInstances)
                        _knownInstances.Add(id);
                }
            }
        }

        _logger.LogInformation("State loaded from {Path}: {FragmentCount} fragments, {InstanceCount} known instances",
            path, _fragments.Count, _knownInstances.Count);
    }
}
