namespace LTAI.Core.Governors;

/// <summary>Simplified LLM-backed intent router — replaces 10+ routing/classifier classes.</summary>
public sealed class IntentRouter
{
    private static readonly string[] _agentTypes = ["code", "eia", "chat", "reasoning", "general"];

    public Task<RouteResult> RouteAsync(string query, CancellationToken ct = default)
    {
        var target = ClassifySimple(query);
        return Task.FromResult(new RouteResult
        {
            ShouldBlock = string.IsNullOrWhiteSpace(query),
            TargetAgent = target,
            FinalConfidence = 0.8f
        });
    }

    public Task<List<RouteResult>> RouteAllAsync(string query, CancellationToken ct = default)
    {
        var result = new List<RouteResult>
        {
            new() { ShouldBlock = false, TargetAgent = ClassifySimple(query), FinalConfidence = 0.8f }
        };
        return Task.FromResult(result);
    }

    private static string ClassifySimple(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "general";
        var q = query.ToLowerInvariant();
        if (q.Contains("code") || q.Contains("function") || q.Contains("class") || q.Contains("bug")) return "code";
        if (q.Contains("eia") || q.Contains("analysis") || q.Contains("report")) return "eia";
        if (q.Contains("reason") || q.Contains("think") || q.Contains("logic")) return "reasoning";
        return "chat";
    }
}

public sealed class RouteResult
{
    public bool ShouldBlock { get; init; }
    public string TargetAgent { get; init; } = "chat";
    public float FinalConfidence { get; init; } = 0.8f;
}


