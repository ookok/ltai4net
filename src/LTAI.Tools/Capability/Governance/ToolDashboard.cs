using System.Collections.Concurrent;

namespace LTAI.Tools.Capability.Governance;

public sealed class ToolDashboard
{
    private readonly ToolLifecycle _lifecycle = ToolLifecycle.Instance;
    private readonly ConcurrentDictionary<string, long> _callLatencyMs = new();

    public void RecordCall(string toolName, long latencyMs, bool success)
    {
        _lifecycle.RecordInvocation(toolName, success);
        _callLatencyMs.AddOrUpdate(toolName, latencyMs, (_, old) => (old + latencyMs) / 2);
    }

    public ToolDashboardReport GetReport()
    {
        var all = _lifecycle.GetAll();
        var active = _lifecycle.GetActive();
        var deprecated = _lifecycle.GetDeprecated();
        var failing = _lifecycle.GetFailing(0.5, 5);

        return new ToolDashboardReport
        {
            TotalTools = all.Count,
            ActiveTools = active.Count,
            DeprecatedTools = deprecated.Count,
            FailingTools = failing.Select(f => new ToolHealthEntry
            {
                Name = f.Name,
                SuccessRate = f.SuccessRate,
                Invocations = f.InvocationCount,
                Errors = f.ErrorCount,
                State = f.State.ToString(),
                AvgLatencyMs = _callLatencyMs.GetValueOrDefault(f.Name)
            }).ToList(),
            TopByUsage = active
                .OrderByDescending(a => a.InvocationCount)
                .Take(10)
                .Select(a => new ToolHealthEntry
                {
                    Name = a.Name,
                    SuccessRate = a.SuccessRate,
                    Invocations = a.InvocationCount,
                    Errors = a.ErrorCount,
                    State = a.State.ToString()
                }).ToList(),
            DeprecatedWithReplacement = deprecated
                .Where(d => d.Replacement != null)
                .Select(d => new { d.Name, d.Replacement, d.DeprecationMessage })
                .ToList<object>()
        };
    }

    public Dictionary<string, object?> GetHealthSummary()
    {
        var report = GetReport();
        return new Dictionary<string, object?>
        {
            ["total"] = report.TotalTools,
            ["active"] = report.ActiveTools,
            ["deprecated"] = report.DeprecatedTools,
            ["failing"] = report.FailingTools.Count,
            ["healthy_rate"] = report.TotalTools > 0
                ? (double)report.ActiveTools / report.TotalTools
                : 0.0,
            ["alerts"] = report.FailingTools.Select(f => new { f.Name, f.SuccessRate }).ToList()
        };
    }
}

public sealed class ToolDashboardReport
{
    public int TotalTools { get; init; }
    public int ActiveTools { get; init; }
    public int DeprecatedTools { get; init; }
    public List<ToolHealthEntry> FailingTools { get; init; } = new();
    public List<ToolHealthEntry> TopByUsage { get; init; } = new();
    public List<object> DeprecatedWithReplacement { get; init; } = new();
}

public sealed class ToolHealthEntry
{
    public string Name { get; init; } = "";
    public double SuccessRate { get; init; }
    public int Invocations { get; init; }
    public int Errors { get; init; }
    public string State { get; init; } = "";
    public long AvgLatencyMs { get; init; }
}
