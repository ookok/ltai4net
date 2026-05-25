using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Middleware;

public sealed class BudgetTrackingMiddleware
{
    private readonly ILogger<BudgetTrackingMiddleware> _logger;
    private readonly ConcurrentDictionary<string, AgentBudget> _budgets = new();
    private readonly int _dailyTokenLimit;
    private readonly decimal _dailyCostLimitUsd;
    private readonly Dictionary<string, decimal> _modelCostPer1kTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-v4-pro"] = 0.002m,
        ["deepseek-v4-flash"] = 0.0005m,
        ["gpt-4o"] = 0.015m,
        ["qwen-max"] = 0.004m,
        ["local-onnx"] = 0m
    };

    public BudgetTrackingMiddleware(ILogger<BudgetTrackingMiddleware> logger, int dailyTokenLimit = 100_000, decimal dailyCostLimitUsd = 10.00m)
    {
        _logger = logger;
        _dailyTokenLimit = dailyTokenLimit;
        _dailyCostLimitUsd = dailyCostLimitUsd;
    }

    public async Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var agentName = innerAgent.Name ?? "unknown";
        var today = DateTime.UtcNow.Date;
        var budget = _budgets.GetOrAdd(agentName, _ => new AgentBudget { Date = today });

        if (budget.Date != today)
        {
            budget = new AgentBudget { Date = today };
            _budgets[agentName] = budget;
        }

        var estimatedInputTokens = EstimateTokens(messages);
        var modelName = options?.AdditionalProperties?.TryGetValue("model", out var m) == true ? m?.ToString() : "deepseek-v4-pro";
        var costPer1k = _modelCostPer1kTokens.GetValueOrDefault(modelName ?? "", 0.002m);
        var estimatedCost = estimatedInputTokens / 1000m * costPer1k;

        if (budget.TotalTokens + estimatedInputTokens > _dailyTokenLimit)
        {
            _logger.LogWarning("BudgetTracking: Agent '{Agent}' exceeded daily token limit ({Used}/{Limit})",
                agentName, budget.TotalTokens, _dailyTokenLimit);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"[Budget] Daily token limit of {_dailyTokenLimit:N0} reached for '{agentName}'. Used: {budget.TotalTokens:N0}. Please try again tomorrow or switch to a cheaper model."));
        }

        if (budget.TotalCost + estimatedCost > _dailyCostLimitUsd)
        {
            _logger.LogWarning("BudgetTracking: Agent '{Agent}' exceeded daily cost limit (${Used:F2}/${Limit:F2})",
                agentName, budget.TotalCost, _dailyCostLimitUsd);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"[Budget] Daily cost limit of ${_dailyCostLimitUsd:F2} would be exceeded. Current: ${budget.TotalCost:F2}, Estimated: ${estimatedCost:F4}. Try a cheaper model or wait until tomorrow."));
        }

        budget.TotalTokens += estimatedInputTokens;
        budget.TotalCost += estimatedCost;
        budget.RequestCount++;

        _logger.LogDebug("BudgetTracking: Agent '{Agent}' tokens={Tokens} cost=${Cost:F4} reqs={Reqs}",
            agentName, budget.TotalTokens, budget.TotalCost, budget.RequestCount);

        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);

        var outputTokens = EstimateTokensFromResponse(response);
        budget.TotalTokens += outputTokens;
        budget.TotalCost += outputTokens / 1000m * costPer1k;

        return response;
    }

    public AgentBudget GetBudget(string agentName) => _budgets.GetValueOrDefault(agentName, new AgentBudget());

    public Dictionary<string, AgentBudget> GetAllBudgets() => _budgets.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static int EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        var totalChars = 0;
        foreach (var msg in messages)
            totalChars += msg.Text?.Length ?? 0;
        return (int)(totalChars / 3.5);
    }

    private static int EstimateTokensFromResponse(AgentResponse response)
    {
        var chars = response.Text?.Length ?? 0;
        foreach (var msg in response.Messages ?? Enumerable.Empty<ChatMessage>())
            chars += msg.Text?.Length ?? 0;
        return (int)(chars / 3.5);
    }
}

public sealed class AgentBudget
{
    public DateTime Date { get; init; } = DateTime.UtcNow.Date;
    public int TotalTokens { get; set; }
    public decimal TotalCost { get; set; }
    public int RequestCount { get; set; }
}
