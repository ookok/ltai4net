using LTAI.Core.Configuration;

namespace LTAI.Agent.Scheduling;

public sealed class CapacityPlanner
{
    private readonly int _contextWindow;

    public CapacityPlanner(int contextWindow = 64000)
    {
        _contextWindow = contextWindow;
    }

    public int TokenBudget => _contextWindow;
    public int UsedTokens => (int)UsageTracker.PromptTokens;
    public int AvailableTokens => Math.Max(0, _contextWindow - UsedTokens);
    public double UsageRatio => Math.Clamp((double)UsedTokens / _contextWindow, 0, 1);

    public int EstimateToolCapacity(int avgToolTokens = 200)
    {
        var available = AvailableTokens;
        return avgToolTokens > 0 ? available / avgToolTokens : 0;
    }

    public int EstimateTurnCapacity(int avgTurnTokens = 500)
    {
        var available = AvailableTokens;
        var slackTokens = (int)(_contextWindow * 0.2);
        available = Math.Max(0, available - slackTokens);
        return avgTurnTokens > 0 ? available / avgTurnTokens : 0;
    }

    public (int toolsRemaining, int turnsRemaining, double usagePct) Snapshot(int avgToolTokens = 200, int avgTurnTokens = 500)
        => (EstimateToolCapacity(avgToolTokens), EstimateTurnCapacity(avgTurnTokens), UsageRatio);

    public string Summary(int avgToolTokens = 200, int avgTurnTokens = 500)
    {
        var (tools, turns, pct) = Snapshot(avgToolTokens, avgTurnTokens);
        return $"Context: {UsedTokens:N0}/{TokenBudget:N0} ({pct:P1}) | ~{tools} tools | ~{turns} turns remaining";
    }
}
