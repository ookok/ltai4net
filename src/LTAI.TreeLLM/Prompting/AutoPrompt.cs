using System.Collections.Concurrent;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Prompting;

public sealed class AutoPrompt
{
    private const int MutationInterval = 50;
    private const int PruneInterval = 200;
    private const int MaxVariants = 20;
    private const double LowQualityThreshold = 0.2;

    private static readonly Lazy<AutoPrompt> LazyInstance = new(() => new AutoPrompt());
    public static AutoPrompt Instance => LazyInstance.Value;

    private static readonly Dictionary<string, string> DefaultPrompts = new()
    {
        ["general"] = "",
        ["code"] = "Write clean, well-documented code with error handling.",
        ["chat"] = "Keep responses friendly and concise.",
        ["reasoning"] = "Think step by step. Show your reasoning before concluding.",
    };

    private static readonly string[] StrategyPool =
    [
        "Be concise and direct.",
        "Use examples to illustrate.",
        "Provide step-by-step reasoning.",
        "Consider edge cases and limitations.",
        "Structure output with clear headings."
    ];

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PromptVariant>> _pool = new();
    private readonly ConcurrentDictionary<string, int> _callCounts = new();
    private readonly ConcurrentDictionary<string, int> _variantCounters = new();
    private readonly ConcurrentDictionary<string, string> _lastSelected = new();
    private readonly Random _rng = new();

    public AutoPrompt()
    {
    }

    public (string Text, string VariantId) Select(string taskType)
    {
        var variants = _pool.GetOrAdd(taskType, _ =>
        {
            var dict = new ConcurrentDictionary<string, PromptVariant>();
            if (DefaultPrompts.TryGetValue(taskType, out var defaultText))
            {
                dict.TryAdd("v0", new PromptVariant
                {
                    Id = "v0",
                    Text = defaultText,
                    Alpha = 3.0,
                    Beta = 3.0
                });
            }
            if (!DefaultPrompts.ContainsKey(taskType))
            {
                dict.TryAdd("v0", new PromptVariant
                {
                    Id = "v0",
                    Text = "",
                    Alpha = 3.0,
                    Beta = 3.0
                });
            }
            return dict;
        });

        var count = _callCounts.AddOrUpdate(taskType, 1, (_, v) => v + 1);
        if (count % MutationInterval == 0)
            Mutate(taskType, variants);
        if (count % PruneInterval == 0)
            Prune(taskType, variants);

        PromptVariant? best = null;
        var bestScore = double.MinValue;
        var localRng = new Random(Interlocked.Increment(ref _rngSeed));

        foreach (var variant in variants.Values)
        {
            var score = BetaBelief.SampleBeta(localRng, variant.Alpha, variant.Beta);
            if (score > bestScore)
            {
                bestScore = score;
                best = variant;
            }
        }

        if (best == null)
        {
            var fallback = GetOrAddBaseVariant(taskType);
            _lastSelected[taskType] = fallback.Id;
            return (fallback.Text, fallback.Id);
        }

        _lastSelected[taskType] = best.Id;
        return (best.Text, best.Id);
    }

    public void Feedback(string taskType, string variantId, double quality)
    {
        quality = Math.Max(0.0, Math.Min(1.0, quality));

        var variants = _pool.GetOrAdd(taskType, _ => new ConcurrentDictionary<string, PromptVariant>());

        variants.AddOrUpdate(variantId,
            _ => new PromptVariant
            {
                Id = variantId,
                Text = "",
                Alpha = 3.0 + quality * 3.0,
                Beta = 3.0 + (1.0 - quality) * 3.0
            },
            (_, existing) =>
            {
                existing.Alpha += quality * 3.0;
                existing.Beta += (1.0 - quality) * 3.0;
                return existing;
            });
    }

    public string? LastVariant(string taskType)
    {
        _lastSelected.TryGetValue(taskType, out var id);
        return id;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var taskStats = new Dictionary<string, object>();
        foreach (var (taskType, variants) in _pool)
        {
            taskStats[taskType] = new Dictionary<string, object>
            {
                ["variant_count"] = variants.Count,
                ["call_count"] = _callCounts.GetValueOrDefault(taskType, 0),
                ["last_variant"] = LastVariant(taskType) ?? ""
            };
        }

        return new Dictionary<string, object>
        {
            ["task_types"] = _pool.Count,
            ["tasks"] = taskStats
        };
    }

    private PromptVariant GetOrAddBaseVariant(string taskType)
    {
        var variants = _pool.GetOrAdd(taskType, _ => new ConcurrentDictionary<string, PromptVariant>());
        return variants.GetOrAdd("v0", _ => new PromptVariant
        {
            Id = "v0",
            Text = DefaultPrompts.GetValueOrDefault(taskType, ""),
            Alpha = 3.0,
            Beta = 3.0
        });
    }

    private void Prune(string taskType, ConcurrentDictionary<string, PromptVariant> variants)
    {
        if (variants.Count <= MaxVariants) return;

        var keep = variants.Values
            .OrderByDescending(v => v.Alpha / (v.Alpha + v.Beta))
            .Take(MaxVariants)
            .Select(v => v.Id)
            .ToHashSet();

        foreach (var id in variants.Keys)
        {
            if (!keep.Contains(id) && id != "v0")
                variants.TryRemove(id, out _);
        }
    }

    private void Mutate(string taskType, ConcurrentDictionary<string, PromptVariant> variants)
    {
        var counter = _variantCounters.AddOrUpdate(taskType, 1, (_, v) => v + 1);
        var strategy = StrategyPool[_rng.Next(StrategyPool.Length)];
        var newId = $"v{counter}";

        var @base = variants.Values.FirstOrDefault()?.Text ?? "";
        var newText = string.IsNullOrEmpty(@base)
            ? strategy
            : $"{@base} {strategy}";

        variants.TryAdd(newId, new PromptVariant
        {
            Id = newId,
            Text = newText,
            Alpha = 3.0,
            Beta = 3.0
        });
    }

    private int _rngSeed;
}
