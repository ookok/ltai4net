using System.Collections.Concurrent;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Quality;

public sealed class ThompsonDelegator
{
    private static readonly Lazy<ThompsonDelegator> _instance = new(() => new ThompsonDelegator());
    public static ThompsonDelegator Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, AgentBelief> _beliefs = new();
    private readonly Random _rng = new();
    private readonly Lock _rngLock = new();
    private readonly ILogger<ThompsonDelegator> _logger;

    private ThompsonDelegator()
    {
        _logger = NullLogger<ThompsonDelegator>.Instance;
    }

    internal ThompsonDelegator(ILogger<ThompsonDelegator> logger)
    {
        _logger = logger ?? NullLogger<ThompsonDelegator>.Instance;
    }

    public string SelectAgent(List<string> candidates, int topK = 1)
    {
        if (candidates == null || candidates.Count == 0)
            return "";

        topK = Math.Max(1, topK);
        topK = Math.Min(topK, candidates.Count);

        var scored = new List<(string agent, double sample)>(candidates.Count);
        foreach (var agent in candidates)
        {
            var belief = _beliefs.GetOrAdd(agent, _ => new AgentBelief { Name = agent });
            double sample;
            lock (_rngLock)
            {
                sample = SampleBeta(_rng, belief.Alpha, belief.Beta);
            }
            scored.Add((agent, sample));
        }

        var bestAgent = scored
            .OrderByDescending(x => x.sample)
            .Take(topK)
            .First()
            .agent;

        _logger.LogDebug("Thompson selected {Agent} from {Count} candidates", bestAgent, candidates.Count);
        return bestAgent;
    }

    public void UpdateOnSuccess(string agent, int tokens)
    {
        _beliefs.AddOrUpdate(agent,
            _ =>
            {
                var b = new AgentBelief { Name = agent, Alpha = 2 };
                b.MarginalTokens += tokens;
                b.LastDelegated = DateTime.UtcNow;
                b.DelegationCount = 1;
                return b;
            },
            (_, b) =>
            {
                b.Alpha += 1;
                b.MarginalTokens += tokens;
                b.LastDelegated = DateTime.UtcNow;
                b.DelegationCount += 1;
                return b;
            });

        _logger.LogDebug("Thompson update success for {Agent}", agent);
    }

    public void UpdateOnFailure(string agent, int tokens)
    {
        _beliefs.AddOrUpdate(agent,
            _ =>
            {
                var b = new AgentBelief { Name = agent, Beta = 2 };
                b.MarginalTokens += tokens;
                b.DelegationCount = 1;
                return b;
            },
            (_, b) =>
            {
                b.Beta += 1;
                b.MarginalTokens += tokens;
                b.DelegationCount += 1;
                return b;
            });

        _logger.LogDebug("Thompson update failure for {Agent}", agent);
    }

    public void ReflectAndUpdate(string agent, bool success, int tokens, string? reflection = null)
    {
        if (success)
        {
            UpdateOnSuccess(agent, tokens);
            return;
        }

        UpdateOnFailure(agent, tokens);

        if (!string.IsNullOrEmpty(reflection) && HasIncapabilityKeywords(reflection))
        {
            _beliefs.AddOrUpdate(agent,
                _ =>
                {
                    var b = new AgentBelief { Name = agent, Beta = 2 };
                    b.MarginalTokens += tokens;
                    b.DelegationCount = 1;
                    return b;
                },
                (_, b) =>
                {
                    b.Beta += 1;
                    return b;
                });

            _logger.LogDebug("Thompson extra penalty for {Agent} due to incapability reflection", agent);
        }
    }

    public string GetBestAgent(List<string> candidates)
    {
        if (candidates == null || candidates.Count == 0)
            return "";

        var best = candidates
            .Select(agent =>
            {
                var belief = _beliefs.GetOrAdd(agent, _ => new AgentBelief { Name = agent });
                return (agent, belief.Mean);
            })
            .MaxBy(x => x.Mean);

        return best.agent;
    }

    public Dictionary<string, object?> GetStats()
    {
        var stats = new Dictionary<string, object?>();
        foreach (var kvp in _beliefs)
        {
            stats[kvp.Key] = new Dictionary<string, object?>
            {
                ["Alpha"] = kvp.Value.Alpha,
                ["Beta"] = kvp.Value.Beta,
                ["Mean"] = kvp.Value.Mean,
                ["DelegationCount"] = kvp.Value.DelegationCount,
                ["MarginalTokens"] = kvp.Value.MarginalTokens,
                ["LastDelegated"] = kvp.Value.LastDelegated
            };
        }
        return stats;
    }

    public static double SampleBeta(Random rng, double alpha, double beta)
    {
        var gammaAlpha = SampleGamma(rng, alpha);
        var gammaBeta = SampleGamma(rng, beta);
        return gammaAlpha / (gammaAlpha + gammaBeta);
    }

    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            var u = rng.NextDouble();
            return SampleGamma(rng, shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double x, v;
            do
            {
                x = SampleNormal(rng);
                v = 1.0 + c * x;
            } while (v <= 0.0);

            v = v * v * v;
            var u = rng.NextDouble();

            if (u < 1.0 - 0.0331 * x * x * x * x)
                return d * v;

            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v)))
                return d * v;
        }
    }

    private static double SampleNormal(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static bool HasIncapabilityKeywords(string reflection)
    {
        var lower = reflection.ToLowerInvariant();
        return lower.Contains("cannot")
            || lower.Contains("unable")
            || lower.Contains("failed")
            || lower.Contains("error")
            || reflection.Contains("不支持")
            || reflection.Contains("无法")
            || reflection.Contains("失败")
            || reflection.Contains("不能");
    }
}
