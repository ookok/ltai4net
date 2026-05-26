using System.Collections.Concurrent;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Knowledge.Core;

public sealed class PromptAbTestManager
{
    private readonly PromptService _promptService;
    private readonly ILogger<PromptAbTestManager> _logger;
    private readonly ConcurrentDictionary<string, PromptVariantGroup> _groups = new();
    private int _globalUses;
    private readonly Random _rng = new();

    public PromptAbTestManager(PromptService promptService, ILogger<PromptAbTestManager>? logger = null)
    {
        _promptService = promptService;
        _logger = logger ?? NullLogger<PromptAbTestManager>.Instance;
    }

    public void RegisterGroup(PromptVariantGroup group)
    {
        _groups[group.GroupId] = group;
        _logger.LogInformation("Registered A/B group '{GroupId}' with {Count} variants, algorithm: {Algo}",
            group.GroupId, group.VariantIds.Count, group.Algorithm);
    }

    public AbTestResult SelectBestVariant(string groupId,
        Dictionary<string, string>? variables = null)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return new AbTestResult { GroupId = groupId };

        string selectedId;
        switch (group.Algorithm.ToLowerInvariant())
        {
            case "thompson":
            case "thompson-sampling":
                selectedId = ThompsonSamplingSelect(group);
                break;
            case "ucb1":
                selectedId = Ucb1Select(group);
                break;
            default:
                selectedId = EpsilonGreedySelect(group);
                break;
        }

        Interlocked.Increment(ref _globalUses);

        var rendered = _promptService.Render(selectedId, variables);

        var scores = group.VariantIds.Select(id =>
        {
            var prompt = _promptService.GetById(id);
            var rate = prompt?.Evolution.SuccessRate ?? 0;
            var uses = prompt?.Evolution.TotalUses ?? 0;
            return new VariantScore
            {
                VariantId = id,
                Score = rate,
                SuccessRate = rate,
                TotalUses = uses,
                Rendered = id == selectedId ? rendered.Rendered : null
            };
        }).ToList();

        return new AbTestResult
        {
            GroupId = groupId,
            SelectedVariantId = selectedId,
            AllScores = scores,
            Algorithm = group.Algorithm
        };
    }

    public async Task<List<PromptRenderResult>> RenderAllVariantsAsync(
        string groupId, Dictionary<string, string>? variables = null,
        CancellationToken ct = default)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return new List<PromptRenderResult>();

        var tasks = group.VariantIds.Select(id =>
            Task.Run(() => _promptService.Render(id, variables), ct));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    public void RecordVariantFeedback(string groupId, string variantId, bool success)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;

        if (!group.VariantIds.Contains(variantId))
            return;

        _promptService.RecordFeedback(variantId, success);
        _logger.LogDebug("A/B feedback: group={GroupId} variant={VariantId} success={Success}",
            groupId, variantId, success);
    }

    public VariantScore[] GetVariantStats(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return Array.Empty<VariantScore>();

        return group.VariantIds.Select(id =>
        {
            var prompt = _promptService.GetById(id);
            var rate = prompt?.Evolution.SuccessRate ?? 0;
            var uses = prompt?.Evolution.TotalUses ?? 0;
            return new VariantScore
            {
                VariantId = id,
                Score = rate,
                SuccessRate = rate,
                TotalUses = uses
            };
        }).ToArray();
    }

    public List<PromptVariantGroup> GetGroupsByDomain(string domain)
    {
        return _groups.Values
            .Where(g => g.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string EpsilonGreedySelect(PromptVariantGroup group)
    {
        var variants = group.VariantIds;

        foreach (var id in variants)
        {
            var prompt = _promptService.GetById(id);
            if (prompt != null && prompt.Evolution.TotalUses == 0)
                return id;
        }

        if (_rng.NextDouble() < group.ExplorationRate)
            return variants[_rng.Next(variants.Count)];

        string bestId = variants[0];
        double bestRate = 0;

        foreach (var id in variants)
        {
            var prompt = _promptService.GetById(id);
            var rate = prompt?.Evolution.SuccessRate ?? 0;
            if (rate > bestRate)
            {
                bestRate = rate;
                bestId = id;
            }
        }

        return bestId;
    }

    private string ThompsonSamplingSelect(PromptVariantGroup group)
    {
        var variants = group.VariantIds;
        string bestId = variants[0];
        double bestScore = 0;

        foreach (var id in variants)
        {
            var prompt = _promptService.GetById(id);
            var a = (prompt?.Evolution.SuccessCount ?? 0) + 1;
            var b = (prompt?.Evolution.FailureCount ?? 0) + 1;

            var gammaA = MarsagliaGamma(a);
            var gammaB = MarsagliaGamma(b);
            var score = gammaA / (gammaA + gammaB);

            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestId;
    }

    private string Ucb1Select(PromptVariantGroup group)
    {
        var variants = group.VariantIds;
        var globalUses = Volatile.Read(ref _globalUses);
        if (globalUses == 0) globalUses = 1;

        string bestId = variants[0];
        double bestScore = double.MinValue;

        foreach (var id in variants)
        {
            var prompt = _promptService.GetById(id);
            var uses = prompt?.Evolution.TotalUses ?? 0;

            if (uses == 0)
                return id;

            var rate = prompt?.Evolution.SuccessRate ?? 0;
            var exploration = Math.Sqrt(2 * Math.Log(globalUses) / uses);
            var score = rate + exploration;

            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestId;
    }

    private double MarsagliaGamma(double shape)
    {
        if (shape < 1)
        {
            var u = _rng.NextDouble();
            return MarsagliaGamma(shape + 1) * Math.Pow(u, 1.0 / shape);
        }

        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double x, v;
            do
            {
                x = SampleGaussian();
                v = 1 + c * x;
            } while (v <= 0);

            v = v * v * v;
            var u = _rng.NextDouble();

            if (u < 1 - 0.0331 * x * x * x * x)
                return d * v;

            if (Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v)))
                return d * v;
        }
    }

    private double SampleGaussian()
    {
        double u1, u2, s;
        do
        {
            u1 = 2.0 * _rng.NextDouble() - 1.0;
            u2 = 2.0 * _rng.NextDouble() - 1.0;
            s = u1 * u1 + u2 * u2;
        } while (s >= 1.0 || s == 0.0);

        var factor = Math.Sqrt(-2.0 * Math.Log(s) / s);
        return u1 * factor;
    }
}
