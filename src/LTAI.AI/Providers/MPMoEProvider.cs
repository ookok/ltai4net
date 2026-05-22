using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Providers;

/// MP-MoE: Breaking the Echo Chamber — Dynamic Ensemble Pruning for Provider Routing
/// Paper: "Breaking the Echo Chamber: A Dynamic Ensemble Pruning Perspective on MoE" (ICML 2026)
/// Code: https://github.com/kxlkxl1999/MP-MoE
///
/// Core insight: In multi-provider routing, providers often produce redundant responses
/// (the "echo chamber" effect). MP-MoE breaks this by dynamically pruning co-occurring
/// and similar providers using Optimal Transport principles.

/// Echo detection result for a provider pair
public sealed record EchoDetectResult
{
    public string ProviderA { get; init; } = "";
    public string ProviderB { get; init; } = "";
    public double Similarity { get; init; }
    public double CoOccurrence { get; init; }
    public bool IsEcho => Similarity > 0.85 && CoOccurrence > 0.7;
}

/// Provider pruning decision
public sealed record ProviderPruneDecision
{
    public string Provider { get; init; } = "";
    public bool ShouldPrune { get; init; }
    public string Reason { get; init; } = "";
    public double RedundancyScore { get; init; }
}

/// Co-occurrence Echo Detector — tracks which provider pairs always fire together,
/// detecting the "echo chamber" where multiple providers return identical responses.
public sealed class CoEchoDetector
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _coFires = new();
    private readonly ConcurrentDictionary<string, int> _providerFires = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _lastResponses = new();
    private readonly ILogger<CoEchoDetector> _logger;
    private double _totalQueries;

    public CoEchoDetector(ILogger<CoEchoDetector>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CoEchoDetector>.Instance;
    }

    /// Record a provider's response for similarity comparison
    public void RecordResponse(string provider, string response)
    {
        _providerFires.AddOrUpdate(provider, 1, (_, c) => c + 1);
        _totalQueries++;

        var fingerprint = HashResponse(response);
        _lastResponses.AddOrUpdate(provider,
            _ => new HashSet<string> { fingerprint },
            (_, set) => { set.Add(fingerprint); if (set.Count > 3) set.Clear(); set.Add(fingerprint); return set; });
    }

    /// Record a co-fire: both providers responded to the same query
    public void RecordCoFire(string providerA, string providerB)
    {
        if (providerA == providerB) return;

        void Update(string a, string b)
        {
            _coFires.GetOrAdd(a, _ => new ConcurrentDictionary<string, int>())
                    .AddOrUpdate(b, 1, (_, c) => c + 1);
        }

        Update(providerA, providerB);
        Update(providerB, providerA);
    }

    /// Compute similarity between two providers based on response overlap
    public double ComputeSimilarity(string providerA, string providerB)
    {
        var fpA = _lastResponses.GetValueOrDefault(providerA);
        var fpB = _lastResponses.GetValueOrDefault(providerB);
        if (fpA == null || fpB == null || fpA.Count == 0 || fpB.Count == 0)
            return 0;

        var intersect = fpA.Intersect(fpB).Count();
        var union = fpA.Union(fpB).Count();
        return union > 0 ? (double)intersect / union : 0;
    }

    /// Compute co-occurrence rate between two providers
    public double ComputeCoOccurrence(string providerA, string providerB)
    {
        var coFire = _coFires.GetValueOrDefault(providerA)?.GetValueOrDefault(providerB) ?? 0;
        var fireA = _providerFires.GetValueOrDefault(providerA, 1);
        return fireA > 0 ? (double)coFire / fireA : 0;
    }

    /// Detect echo chambers: find provider pairs that are highly similar + co-occurring
    public List<EchoDetectResult> DetectEchoes()
    {
        var echoes = new List<EchoDetectResult>();
        var providers = _providerFires.Keys.ToList();

        for (int i = 0; i < providers.Count; i++)
        {
            for (int j = i + 1; j < providers.Count; j++)
            {
                var sim = ComputeSimilarity(providers[i], providers[j]);
                var co = ComputeCoOccurrence(providers[i], providers[j]);

                if (sim > 0.6 || co > 0.5)
                {
                    echoes.Add(new EchoDetectResult
                    {
                        ProviderA = providers[i],
                        ProviderB = providers[j],
                        Similarity = sim,
                        CoOccurrence = co
                    });
                }
            }
        }

        echoes = echoes.OrderByDescending(e => e.Similarity + e.CoOccurrence).ToList();
        if (echoes.Count > 0)
            _logger.LogDebug("CoEchoDetector: found {Count} echo pairs among {N} providers", echoes.Count, providers.Count);

        return echoes;
    }

    /// Get pruning decision: which provider to prune from a redundant pair
    public ProviderPruneDecision GetPruneDecision(string providerA, string providerB)
    {
        var fireA = _providerFires.GetValueOrDefault(providerA, 1);
        var fireB = _providerFires.GetValueOrDefault(providerB, 1);

        // Prune the less-frequently-used one
        if (fireB < fireA)
            return new ProviderPruneDecision { Provider = providerB, ShouldPrune = true, Reason = $"Less frequent than {providerA} ({fireB} vs {fireA})", RedundancyScore = ComputeSimilarity(providerA, providerB) };

        return new ProviderPruneDecision { Provider = providerA, ShouldPrune = true, Reason = $"Less frequent than {providerB} ({fireA} vs {fireB})", RedundancyScore = ComputeSimilarity(providerA, providerB) };
    }

    public IReadOnlyDictionary<string, int> ProviderStats => new Dictionary<string, int>(_providerFires);

    private static string HashResponse(string response)
    {
        if (string.IsNullOrEmpty(response)) return "empty";
        const int hashSize = 4;
        var truncated = response.Length > 200 ? response[..200] : response;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(truncated)))[..hashSize];
    }
}

/// Optimal Transport Enhanced Provider Selector
/// Uses OT-inspired scoring to select the minimal set of providers that maximize
/// response diversity while minimizing cost redundancy.
public sealed class OTESelector
{
    private readonly CoEchoDetector _echoDetector;
    private readonly ILogger<OTESelector> _logger;

    public OTESelector(CoEchoDetector? echoDetector = null, ILogger<OTESelector>? logger = null)
    {
        _echoDetector = echoDetector ?? new CoEchoDetector();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OTESelector>.Instance;
    }

    public CoEchoDetector EchoDetector => _echoDetector;

    /// OT-based selection: given N providers, select K that maximize diversity/cost ratio.
    /// This simulates the OT transport plan by greedily selecting the most "informative"
    /// (least redundant) providers.
    /// Returns: ordered list of provider names to use, pruned providers are excluded.
    public (List<string> selected, List<ProviderPruneDecision> pruned) SelectProviders(
        List<string> availableProviders,
        int maxProviders = 3)
    {
        if (availableProviders.Count <= maxProviders)
            return (availableProviders, new());

        var echoes = _echoDetector.DetectEchoes();
        var pruned = new List<ProviderPruneDecision>();
        var selected = new HashSet<string>();

        // Phase 1: detect redundant pairs and prune one from each
        var pruneSet = new HashSet<string>();
        foreach (var echo in echoes.Where(e => e.IsEcho))
        {
            var decision = _echoDetector.GetPruneDecision(echo.ProviderA, echo.ProviderB);
            if (!pruneSet.Contains(decision.Provider))
            {
                pruned.Add(decision);
                pruneSet.Add(decision.Provider);
            }
        }

        // Phase 2: greedy diversity selection from non-pruned providers
        var remaining = availableProviders.Where(p => !pruneSet.Contains(p)).ToList();

        // Pick first provider (most frequently used)
        var stats = _echoDetector.ProviderStats;
        var ordered = remaining.OrderByDescending(p => stats.GetValueOrDefault(p, 0)).ToList();
        selected.Add(ordered[0]);

        // Pick subsequent providers with minimum similarity to already-selected
        while (selected.Count < maxProviders && selected.Count < ordered.Count)
        {
            var best = ordered
                .Where(p => !selected.Contains(p))
                .OrderBy(p => selected.Min(s => _echoDetector.ComputeSimilarity(p, s)))
                .First();

            selected.Add(best);
        }

        _logger.LogDebug("OTESelector: {Total} providers → {Selected} selected, {Pruned} pruned",
            availableProviders.Count, selected.Count, pruned.Count);

        return (selected.ToList(), pruned);
    }

    /// Estimate the information gain of NOT pruning a provider
    public double EstimateRedundancyCost(string provider, List<string> selected)
    {
        if (selected.Count == 0) return 0;
        return selected.Max(s => _echoDetector.ComputeSimilarity(provider, s));
    }
}
