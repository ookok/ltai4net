using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LTAI.Planning.Planning;

public sealed class CostAware
{
    private readonly Dictionary<string, (double input, double output)> _pricePer1M;
    private readonly Dictionary<string, string> _degradationChain;

    private double _dailyBudget = 10.00;
    private readonly List<TokenUsage> _usage = new();
    private readonly Dictionary<string, int> _sessionBudget = new();
    private readonly ConcurrentDictionary<string, double> _sessionCost = new();
    private double _totalCostYuan;
    private readonly Lock _lock = new();
    private readonly ILogger _logger;

    public CostAware(IOptions<LTAIOptions> options, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger<CostAware>.Instance;

        var pricing = options.Value.ModelPricing;
        _pricePer1M = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in pricing.InputPer1M)
        {
            var output = pricing.OutputPer1M.TryGetValue(kvp.Key, out var o) ? o : kvp.Value * 2.0;
            _pricePer1M[kvp.Key] = (kvp.Value, output);
        }
        if (!_pricePer1M.ContainsKey("default"))
            _pricePer1M["default"] = (0.50, 2.00);

        _degradationChain = new Dictionary<string, string>(pricing.DegradationChain, StringComparer.OrdinalIgnoreCase);
    }

    internal CostAware()
        : this(Options.Create(new LTAIOptions()), null)
    {
    }

    public void Record(string model, int tokens, double speedupRatio = 1.0)
    {
        var prices = _pricePer1M.TryGetValue(model, out var p) ? p : _pricePer1M["default"];
        var avgPrice = (prices.input + prices.output) / 2.0;
        var speedup = speedupRatio <= 0 ? 1.0 : speedupRatio;
        var cost = tokens * avgPrice / 1_000_000.0 / speedup;

        var entry = new TokenUsage
        {
            Timestamp = DateTime.UtcNow,
            Model = model,
            Tokens = tokens,
            CostYuan = cost,
        };

        var cutoff = DateTime.UtcNow.AddHours(-24);

        lock (_lock)
        {
            _usage.Add(entry);
            _totalCostYuan += cost;
            _usage.RemoveAll(u => u.Timestamp < cutoff);
        }
    }

    public bool CanUse(string model, double estimatedTokens)
    {
        var prices = _pricePer1M.TryGetValue(model, out var p) ? p : _pricePer1M["default"];
        var avgPrice = (prices.input + prices.output) / 2.0;
        var estimatedCost = estimatedTokens * avgPrice / 1_000_000.0;

        double usedToday;
        lock (_lock)
        {
            usedToday = _usage.Sum(u => u.CostYuan);
        }

        return (usedToday + estimatedCost) <= _dailyBudget * 0.85;
    }

    public string Degrade(string model)
    {
        return _degradationChain.TryGetValue(model, out var degraded) ? degraded : model;
    }

    public string? DegradedModelFor(string model)
    {
        if (!_degradationChain.TryGetValue(model, out var degraded))
            return null;

        if (CanUse(model, 0))
            return null;

        return degraded;
    }

    public BudgetStatus Status()
    {
        double usedToday;
        bool degraded;

        lock (_lock)
        {
            usedToday = _usage.Sum(u => u.CostYuan);
        }

        degraded = usedToday > _dailyBudget * 0.85;

        return new BudgetStatus
        {
            DailyLimit = _dailyBudget,
            UsedToday = usedToday,
            Degraded = degraded,
            TotalCostYuan = _totalCostYuan,
            DegradedSince = degraded ? DateTime.UtcNow : null,
        };
    }

    public double SessionCost(string sessionId)
    {
        return _sessionCost.TryGetValue(sessionId, out var cost) ? cost : 0;
    }

    public void SetDailyBudget(double budget)
    {
        _dailyBudget = budget;
    }

    public Dictionary<string, object?> GetStats()
    {
        lock (_lock)
        {
            var usedToday = _usage.Sum(u => u.CostYuan);
            var modelBreakdown = _usage
                .GroupBy(u => u.Model)
                .ToDictionary(g => g.Key, g => (object?)g.Sum(u => u.CostYuan));

            return new Dictionary<string, object?>
            {
                ["daily_budget"] = _dailyBudget,
                ["used_today"] = usedToday,
                ["remaining"] = _dailyBudget - usedToday,
                ["usage_pct"] = _dailyBudget > 0 ? usedToday / _dailyBudget : 0,
                ["degraded"] = usedToday > _dailyBudget * 0.85,
                ["total_cost"] = _totalCostYuan,
                ["model_breakdown"] = modelBreakdown,
                ["record_count"] = _usage.Count,
            };
        }
    }
}
