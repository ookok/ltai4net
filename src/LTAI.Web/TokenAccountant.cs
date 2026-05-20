using System.Collections.Concurrent;

namespace LTAI.Web;

public enum AllocationLayer
{
    Router,
    Agent,
    Serving,
    Training
}

public sealed record TokenAllocation
{
    public AllocationLayer Layer { get; init; }
    public string Action { get; init; } = "";
    public int TokensSpent { get; init; }
    public double CostYuan { get; init; }
    public double LatencyMs { get; init; }
    public double BenefitScore { get; init; }
    public double RiskFactor { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string SessionId { get; init; } = "";

    public double MarginalCost => CostYuan + LatencyMs * 0.0001 + RiskFactor * CostYuan;
    public double Roi => MarginalCost > 1e-9 ? BenefitScore / MarginalCost : 0;
}

public sealed class LayerBudget
{
    public AllocationLayer Layer { get; }
    public int TokenBudget { get; set; } = 100_000;
    public int TokensSpent { get; set; }
    public double AvgCostPer1k { get; set; }
    public double AvgBenefit { get; set; }
    public double AvgRoi { get; set; }
    public int AllocationCount { get; set; }

    public LayerBudget(AllocationLayer layer) => Layer = layer;
    public int Remaining => Math.Max(0, TokenBudget - TokensSpent);
    public double Utilization => TokenBudget > 0 ? (double)TokensSpent / TokenBudget : 0;
}

public sealed class PriceVector
{
    public int PingTokenCost { get; set; } = 50;
    public int MaxPingProviders { get; set; } = 5;
    public int PlanTokenCost { get; set; } = 200;
    public int ExecuteTokenCost { get; set; } = 500;
    public int VerifyTokenCost { get; set; } = 300;
    public int DeferCost { get; set; } = 0;
    public double PrefillCostPer1k { get; set; } = 0.001;
    public double DecodeCostPer1k { get; set; } = 0.002;
    public double CacheHitSavings { get; set; } = 0.90;
    public double CongestionMultiplier { get; set; } = 1.0;
    public double MinTraceRoi { get; set; } = 0.5;
    public double RiskFreeRate { get; set; } = 0.01;
    public double MaxRoiTarget { get; set; } = 0.8;
}

public sealed class TokenAccountant
{
    private static readonly Lazy<TokenAccountant> _instance = new(() => new TokenAccountant());
    public static TokenAccountant Instance => _instance.Value;

    private readonly int _totalBudget;
    private int _totalSpent;
    private readonly PriceVector _prices = new();
    private readonly Dictionary<AllocationLayer, LayerBudget> _budgets;
    private readonly List<TokenAllocation> _history = new();
    private readonly ConcurrentDictionary<string, List<TokenAllocation>> _sessionAllocations = new();
    private readonly object _lock = new();

    private TokenAccountant(int totalBudget = 1_000_000)
    {
        _totalBudget = totalBudget;
        _budgets = new Dictionary<AllocationLayer, LayerBudget>
        {
            [AllocationLayer.Router] = new(AllocationLayer.Router) { TokenBudget = totalBudget / 4 },
            [AllocationLayer.Agent] = new(AllocationLayer.Agent) { TokenBudget = totalBudget / 4 },
            [AllocationLayer.Serving] = new(AllocationLayer.Serving) { TokenBudget = totalBudget / 4 },
            [AllocationLayer.Training] = new(AllocationLayer.Training) { TokenBudget = totalBudget / 4 }
        };
    }

    public bool ShouldAllocate(
        AllocationLayer layer,
        string action,
        int estimatedTokens,
        double expectedBenefit,
        double riskFactor = 0.0,
        string sessionId = "")
    {
        var budget = _budgets[layer];

        if (budget.TokensSpent + estimatedTokens > budget.TokenBudget)
            return false;

        var estCost = EstimateCost(layer, action, estimatedTokens);
        var latencyCost = EstimateLatency(layer, action);
        var riskCost = riskFactor * estCost;
        var marginalCost = estCost + latencyCost + riskCost;

        return expectedBenefit >= marginalCost;
    }

    public TokenAllocation RecordAllocation(
        AllocationLayer layer,
        string action,
        int tokensSpent,
        double actualBenefit = 0.0,
        double latencyMs = 0.0,
        double riskFactor = 0.0,
        string sessionId = "")
    {
        var cost = EstimateCost(layer, action, tokensSpent);
        var alloc = new TokenAllocation
        {
            Layer = layer,
            Action = action,
            TokensSpent = tokensSpent,
            CostYuan = cost,
            LatencyMs = latencyMs,
            BenefitScore = actualBenefit,
            RiskFactor = riskFactor,
            SessionId = sessionId
        };

        lock (_lock)
        {
            var budget = _budgets[layer];
            budget.TokensSpent += tokensSpent;
            _totalSpent += tokensSpent;
            budget.AllocationCount++;

            var alpha = 1.0 / (budget.AllocationCount + 1);
            budget.AvgCostPer1k = (1 - alpha) * budget.AvgCostPer1k + alpha * cost * 1000 / Math.Max(1, tokensSpent);
            budget.AvgBenefit = (1 - alpha) * budget.AvgBenefit + alpha * actualBenefit;
            budget.AvgRoi = (1 - alpha) * budget.AvgRoi + alpha * alloc.Roi;

            _history.Add(alloc);
        }

        if (!string.IsNullOrEmpty(sessionId))
            _sessionAllocations.AddOrUpdate(sessionId,
                _ => new List<TokenAllocation> { alloc },
                (_, list) => { list.Add(alloc); return list; });

        return alloc;
    }

    public PriceVector GetPriceVector()
    {
        var utilization = (double)_totalSpent / Math.Max(1, _totalBudget);
        _prices.CongestionMultiplier = 1.0 + Math.Max(0, utilization - 0.5) * 2.0;

        var routerBudget = _budgets[AllocationLayer.Router];
        _prices.MaxPingProviders = routerBudget.AvgRoi > 0.8 ? 8 : 3;

        return _prices;
    }

    public Dictionary<string, double> OptimalLayerSplit()
    {
        var totalRoi = _budgets.Values.Sum(b => b.AvgRoi);
        if (totalRoi <= 0)
            return _budgets.Keys.ToDictionary(k => k.ToString(), _ => 0.25);

        return _budgets.ToDictionary(k => k.Key.ToString(), v => v.Value.AvgRoi / totalRoi);
    }

    public Dictionary<string, object> SessionSummary(string sessionId)
    {
        if (!_sessionAllocations.TryGetValue(sessionId, out var allocs) || allocs.Count == 0)
            return new Dictionary<string, object>
            {
                ["total_tokens"] = 0,
                ["total_cost"] = 0.0,
                ["layers"] = new Dictionary<string, object>()
            };

        var byLayer = new Dictionary<string, (int tokens, double cost, double benefit)>();
        foreach (var a in allocs)
        {
            var key = a.Layer.ToString().ToLower();
            if (!byLayer.ContainsKey(key))
                byLayer[key] = (0, 0, 0);
            var (t, c, b) = byLayer[key];
            byLayer[key] = (t + a.TokensSpent, c + a.CostYuan, b + a.BenefitScore);
        }

        var totalTokens = byLayer.Values.Sum(v => v.tokens);
        var totalCost = byLayer.Values.Sum(v => v.cost);
        var avgBenefit = allocs.Count > 0 ? byLayer.Values.Sum(v => v.benefit) / allocs.Count : 0;

        return new Dictionary<string, object>
        {
            ["total_tokens"] = totalTokens,
            ["total_cost"] = Math.Round(totalCost, 6),
            ["avg_benefit"] = Math.Round(avgBenefit, 3),
            ["layers"] = byLayer.ToDictionary(
                k => k.Key,
                v => (object)new Dictionary<string, object>
                {
                    ["tokens"] = v.Value.tokens,
                    ["cost"] = v.Value.cost,
                    ["benefit"] = v.Value.benefit
                })
        };
    }

    public Dictionary<string, object> GlobalStats()
    {
        return new Dictionary<string, object>
        {
            ["total_budget"] = _totalBudget,
            ["total_spent"] = _totalSpent,
            ["utilization"] = Math.Round((double)_totalSpent / Math.Max(1, _totalBudget), 3),
            ["layers"] = _budgets.ToDictionary(
                k => k.Key.ToString().ToLower(),
                v => (object)new Dictionary<string, object>
                {
                    ["budget"] = v.Value.TokenBudget,
                    ["spent"] = v.Value.TokensSpent,
                    ["utilization"] = Math.Round(v.Value.Utilization, 3),
                    ["avg_roi"] = Math.Round(v.Value.AvgRoi, 3),
                    ["avg_benefit"] = Math.Round(v.Value.AvgBenefit, 3)
                }),
            ["optimal_split"] = OptimalLayerSplit()
        };
    }

    public void ResetSession(string sessionId) => _sessionAllocations.TryRemove(sessionId, out _);

    private double EstimateCost(AllocationLayer layer, string action, int tokens)
    {
        if (layer == AllocationLayer.Serving)
        {
            return action switch
            {
                "prefill" => tokens / 1000.0 * _prices.PrefillCostPer1k,
                "decode" => tokens / 1000.0 * _prices.DecodeCostPer1k,
                "cache_hit" => tokens / 1000.0 * _prices.PrefillCostPer1k * (1 - _prices.CacheHitSavings),
                _ => tokens / 1000.0 * 0.0015 * _prices.CongestionMultiplier
            };
        }
        return tokens / 1000.0 * 0.0015 * _prices.CongestionMultiplier;
    }

    private double EstimateLatency(AllocationLayer layer, string action)
    {
        var base_latency = action switch
        {
            "ping" => 0.001,
            "plan" => 0.005,
            "execute" => 0.01,
            "prefill" => 0.002,
            "decode" => 0.008,
            "cache_hit" => 0.0001,
            _ => 0.005
        };
        return base_latency * _prices.CongestionMultiplier;
    }
}
