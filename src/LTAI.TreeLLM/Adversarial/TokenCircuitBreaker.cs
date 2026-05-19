using LTAI.TreeLLM.Models;
using LTAI.TreeLLM.Routing;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Adversarial;

public sealed class TokenCircuitBreaker
{
    private readonly ILogger<TokenCircuitBreaker> _logger;

    public TokenCircuitBreaker(ILogger<TokenCircuitBreaker> logger)
    {
        _logger = logger;
    }

    public BudgetAllocation Allocate(string requestId, BudgetRouter budgetRouter, int topK, int maxTokens)
    {
        var status = budgetRouter.GetStatus();
        var remaining = Math.Max(0, status.Remaining);
        var limit = Math.Max(0.01, status.DailyBudget);
        var ratio = limit > 0 ? remaining / limit : 1.0;

        BudgetStateEnum state;
        int adjustedTopK;
        int adjustedMaxTokens;
        bool aggregate;

        if (ratio < 0.05)
        {
            state = BudgetStateEnum.Open;
            adjustedTopK = 1;
            adjustedMaxTokens = Math.Min(maxTokens, 512);
            aggregate = false;
        }
        else if (ratio < 0.20)
        {
            state = BudgetStateEnum.Throttled;
            adjustedTopK = Math.Max(1, topK - 1);
            adjustedMaxTokens = Math.Min(maxTokens, 1024);
            aggregate = false;
        }
        else if (ratio < 0.50)
        {
            state = BudgetStateEnum.Warning;
            adjustedTopK = topK;
            adjustedMaxTokens = Math.Min(maxTokens, 2048);
            aggregate = true;
        }
        else
        {
            state = BudgetStateEnum.Normal;
            adjustedTopK = topK;
            adjustedMaxTokens = maxTokens;
            aggregate = true;
        }

        var allocation = new BudgetAllocation
        {
            TopK = adjustedTopK,
            MaxTokens = adjustedMaxTokens,
            Aggregate = aggregate
        };

        _logger.LogInformation(
            "TokenCircuitBreaker [{RequestId}]: state={State} ratio={Ratio:F3} topK={TopK} maxTokens={MaxTokens} aggregate={Aggregate}",
            requestId, state, ratio, adjustedTopK, adjustedMaxTokens, aggregate);

        return allocation;
    }

    public void Actual(string requestId, int tokens)
    {
        _logger.LogInformation("TokenCircuitBreaker [{RequestId}]: actual tokens={Tokens}", requestId, tokens);
    }
}
