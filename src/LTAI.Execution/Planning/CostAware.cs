using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Execution.Planning;

public sealed class CostAware
{
    public static readonly Dictionary<string, (double input, double output)> PricePer1M = new()
    {
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-4o-mini"] = (0.15, 0.60),
        ["claude-sonnet"] = (3.00, 15.00),
        ["claude-haiku"] = (0.25, 1.25),
        ["deepseek-v3"] = (0.27, 1.10),
        ["deepseek-r1"] = (0.55, 2.19),
        ["qwen-max"] = (0.40, 1.60),
        ["qwen-turbo"] = (0.08, 0.32),
        ["default"] = (0.50, 2.00),
    };

    public static readonly Dictionary<string, string> DegradationChain = new()
    {
        ["gpt-4o"] = "gpt-4o-mini",
        ["claude-sonnet"] = "claude-haiku",
        ["deepseek-r1"] = "deepseek-v3",
        ["qwen-max"] = "qwen-turbo",
    };

    private static readonly Lazy<CostAware> _instance = new(() =>
        new CostAware((ILogger)NullLogger<CostAware>.Instance));

    public static CostAware Instance => _instance.Value;

    private double _dailyBudget = 10.00;
    private readonly List<TokenUsage> _usage = new();
    private readonly Dictionary<string, int> _sessionBudget = new();
    private readonly ConcurrentDictionary<string, double> _sessionCost = new();
    private double _totalCostYuan;
    private readonly Lock _lock = new();
    private readonly ILogger _logger;

    public CostAware(ILogger logger)
    {
        _logger = logger;
    }

    internal CostAware()
        : this((ILogger)NullLogger<CostAware>.Instance)
    {
    }

    public void Record(string model, int tokens, double speedupRatio = 1.0)
    {
        var prices = PricePer1M.TryGetValue(model, out var p) ? p : PricePer1M["default"];
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
        var prices = PricePer1M.TryGetValue(model, out var p) ? p : PricePer1M["default"];
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
        return DegradationChain.TryGetValue(model, out var degraded) ? degraded : model;
    }

    public string? DegradedModelFor(string model)
    {
        if (!DegradationChain.TryGetValue(model, out var degraded))
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
