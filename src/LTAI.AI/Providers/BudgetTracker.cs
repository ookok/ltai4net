using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Providers;

public sealed class BudgetTracker
{
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<BudgetTracker> _logger;
    private decimal _dailySpent;
    private DateTime _lastResetUtc = DateTime.UtcNow.Date;
    private readonly object _budgetLock = new();

    public decimal DailySpent
    {
        get { lock (_budgetLock) return _dailySpent; }
    }

    public BudgetTracker(IOptions<LTAIOptions> options, ILogger<BudgetTracker> logger)
    {
        _options = options;
        _logger = logger;
    }

    public void CheckBudget()
    {
        var ai = _options.Value.AI;
        if (ai.DailyBudgetUsd <= 0)
            return;

        lock (_budgetLock)
        {
            var today = DateTime.UtcNow.Date;
            if (_lastResetUtc < today)
            {
                _dailySpent = 0;
                _lastResetUtc = today;
                _logger.LogInformation("Budget reset for {Date}", today);
            }

            if (_dailySpent >= ai.DailyBudgetUsd)
                throw new InvalidOperationException(
                    $"Daily budget exceeded: {_dailySpent:F2} / {ai.DailyBudgetUsd} USD. Reset at midnight UTC.");
        }
    }

    public void EstimateCost(int totalTokens, string modelKey)
    {
        var pricing = _options.Value.ModelPricing;
        var inputCostPer1M = pricing.InputPer1M.GetValueOrDefault(modelKey, pricing.InputPer1M.GetValueOrDefault("default", 0.50));
        var outputCostPer1M = pricing.OutputPer1M.GetValueOrDefault(modelKey, pricing.OutputPer1M.GetValueOrDefault("default", 2.00));

        var inputTokens = (int)(totalTokens * 0.3);
        var outputTokens = totalTokens - inputTokens;

        var inputCost = inputTokens / 1_000_000.0 * inputCostPer1M;
        var outputCost = outputTokens / 1_000_000.0 * outputCostPer1M;
        var cost = inputCost + outputCost;

        lock (_budgetLock)
        {
            _dailySpent += (decimal)cost;
        }
        _logger.LogDebug("Model: {Model}, cost: ${Cost:F4}, daily: ${Daily:F2}", modelKey, cost, _dailySpent);
    }
}
