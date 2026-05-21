using LTAI.Core.Configuration;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed record CostAwareRouteDecision
{
    public string SelectedModel { get; init; } = "";
    public double EstimatedCostYuan { get; init; }
    public double EstimatedTokens { get; init; }
    public TrilemmaVector Trilemma { get; init; } = new();
    public bool ShouldUseLocal { get; init; }
    public string Reason { get; init; } = "";
    public EconomicPolicy Policy { get; init; } = EconomicPolicy.Balanced();
}

public sealed class CostAwareRouter
{
    private readonly IOptions<LTAIOptions> _options;
    private readonly EconomicPolicy _policy;
    private readonly ILogger<CostAwareRouter> _logger;
    private double _dailySpentYuan;
    private DateTime _dayStart = DateTime.UtcNow.Date;

    private static readonly Dictionary<string, double> ModelCostPer1KTokens = new()
    {
        ["deepseek-v4-pro"] = 0.10,
        ["deepseek-v4-flash"] = 0.01,
        ["deepseek-v3"] = 0.02,
        ["gpt-4o"] = 0.03,
        ["gpt-4o-mini"] = 0.005,
        ["claude-sonnet-4"] = 0.03,
        ["claude-haiku"] = 0.008,
    };

    public CostAwareRouter(
        IOptions<LTAIOptions> options,
        ILogger<CostAwareRouter>? logger = null)
    {
        _options = options;
        _policy = DeterminePolicy(options.Value);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CostAwareRouter>.Instance;
    }

    public CostAwareRouteDecision Decide(string query, float complexity, float localConfidence, bool hasLocalAnswer)
    {
        ResetDailyBudgetIfNewDay();

        var estimatedTokens = EstimateTokens(query);
        var remainingBudget = _policy.MaxDailyBudgetYuan - _dailySpentYuan;

        if (hasLocalAnswer && localConfidence >= 0.7f)
        {
            return new CostAwareRouteDecision
            {
                SelectedModel = "local",
                EstimatedCostYuan = 0,
                EstimatedTokens = 0,
                Trilemma = new TrilemmaVector { CostScore = 1.0f, SpeedScore = 1.0f, QualityScore = localConfidence },
                ShouldUseLocal = true,
                Reason = "Local answer available with high confidence"
            };
        }

        var l1Model = _options.Value.AI.L1.Model;
        var l2Model = _options.Value.AI.L2.Model;

        var l1Cost = EstimateCost(l1Model, estimatedTokens);
        var l2Cost = EstimateCost(l2Model, estimatedTokens);

        var l1Trilemma = TrilemmaVector.FromRaw(l1Cost, EstimateLatencyMs(l1Model), PredictQuality("l1", complexity), _policy.MaxDailyBudgetYuan);
        var l2Trilemma = TrilemmaVector.FromRaw(l2Cost, EstimateLatencyMs(l2Model), PredictQuality("l2", complexity), _policy.MaxDailyBudgetYuan);

        var l1Score = l1Trilemma.WeightedScore(_policy);
        var l2Score = l2Trilemma.WeightedScore(_policy);

        if (remainingBudget < l2Cost && l1Cost <= remainingBudget)
        {
            return new CostAwareRouteDecision
            {
                SelectedModel = l1Model,
                EstimatedCostYuan = l1Cost,
                EstimatedTokens = estimatedTokens,
                Trilemma = l1Trilemma,
                ShouldUseLocal = false,
                Reason = "Budget constraint: L2 too expensive, falling back to L1",
                Policy = _policy
            };
        }

        if (complexity < 0.4f && localConfidence >= 0.5f)
        {
            return new CostAwareRouteDecision
            {
                SelectedModel = l1Model,
                EstimatedCostYuan = l1Cost,
                EstimatedTokens = estimatedTokens,
                Trilemma = l1Trilemma,
                ShouldUseLocal = false,
                Reason = "Low complexity query, L1 sufficient",
                Policy = _policy
            };
        }

        if (l2Score > l1Score && remainingBudget >= l2Cost)
        {
            return new CostAwareRouteDecision
            {
                SelectedModel = l2Model,
                EstimatedCostYuan = l2Cost,
                EstimatedTokens = estimatedTokens,
                Trilemma = l2Trilemma,
                ShouldUseLocal = false,
                Reason = "High complexity, L2 justified by quality",
                Policy = _policy
            };
        }

        return new CostAwareRouteDecision
        {
            SelectedModel = l1Model,
            EstimatedCostYuan = l1Cost,
            EstimatedTokens = estimatedTokens,
            Trilemma = l1Trilemma,
            ShouldUseLocal = false,
            Reason = "Cost-optimized: L1 provides best trilemma score",
            Policy = _policy
        };
    }

    public void RecordActualCost(double costYuan)
    {
        _dailySpentYuan += costYuan;
        _logger.LogDebug("Recorded actual cost: {Cost:F4} yuan, daily total: {Daily:F4}", costYuan, _dailySpentYuan);
    }

    public Dictionary<string, object> GetBudgetStatus()
    {
        ResetDailyBudgetIfNewDay();
        return new Dictionary<string, object>
        {
            ["daily_spent_yuan"] = _dailySpentYuan,
            ["daily_budget_yuan"] = _policy.MaxDailyBudgetYuan,
            ["remaining_yuan"] = _policy.MaxDailyBudgetYuan - _dailySpentYuan,
            ["usage_percent"] = (_dailySpentYuan / _policy.MaxDailyBudgetYuan) * 100.0,
            ["policy"] = _policy.ComplianceLevel.ToString(),
            ["weights"] = new { cost = _policy.CostWeight, speed = _policy.SpeedWeight, quality = _policy.QualityWeight }
        };
    }

    private void ResetDailyBudgetIfNewDay()
    {
        var today = DateTime.UtcNow.Date;
        if (today > _dayStart)
        {
            _dailySpentYuan = 0;
            _dayStart = today;
            _logger.LogInformation("Daily budget reset: new day started");
        }
    }

    private static EconomicPolicy DeterminePolicy(LTAIOptions options)
    {
        return options.Economy?.Policy?.ToLowerInvariant() switch
        {
            "economy" => EconomicPolicy.Economy(),
            "quality" => EconomicPolicy.Quality(),
            "speed" => EconomicPolicy.Speed(),
            _ => EconomicPolicy.Balanced()
        };
    }

    private static int EstimateTokens(string query)
    {
        var charCount = query.Length;
        return (int)(charCount * 0.5) + 50;
    }

    private double EstimateCost(string model, int tokens)
    {
        var costPer1K = ModelCostPer1KTokens.GetValueOrDefault(model, 0.02);
        return (tokens / 1000.0) * costPer1K;
    }

    private static double EstimateLatencyMs(string model)
    {
        return model.ToLowerInvariant().Contains("flash") || model.ToLowerInvariant().Contains("mini")
            ? 500
            : model.ToLowerInvariant().Contains("pro") || model.ToLowerInvariant().Contains("sonnet")
                ? 2000
                : 1000;
    }

    private static double PredictQuality(string tier, float complexity)
    {
        return tier switch
        {
            "l1" => Math.Max(0.3, 0.8 - complexity * 0.5),
            "l2" => Math.Max(0.5, 0.95 - complexity * 0.2),
            _ => 0.5
        };
    }
}
