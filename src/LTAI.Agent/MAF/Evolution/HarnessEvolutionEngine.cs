namespace LTAI.Agent.Evolution;

public interface IHarnessComponent
{
    string ComponentName { get; }
    string CurrentHash { get; }
    Task<EvolutionFitness> EvaluateAsync(IServiceProvider sp, CancellationToken ct = default);
    Task ApplyEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct = default);
    Task RollbackEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct = default);
}

public sealed class EvolutionFitness
{
    public double Score { get; init; }
    public int Samples { get; init; }
    public string? ErrorRate { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
}

public sealed class HarnessEvolutionEngine
{
    private readonly List<IHarnessComponent> _components = new();
    private readonly DecisionLog _decisionLog;
    private readonly ExperienceDebugger _debugger;
    private readonly HarnessSnapshot _snapshot;

    public IReadOnlyList<IHarnessComponent> Components => _components.AsReadOnly();

    public HarnessEvolutionEngine(DecisionLog decisionLog, ExperienceDebugger debugger, HarnessSnapshot snapshot)
    {
        _decisionLog = decisionLog;
        _debugger = debugger;
        _snapshot = snapshot;
    }

    public void RegisterComponent(IHarnessComponent component) => _components.Add(component);

    public async Task<EvolutionIterationResult> RunIterationAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var before = _snapshot.Capture();
        var experience = _debugger.Analyze(TimeSpan.FromHours(1));
        var edits = new List<HarnessEdit>();

        foreach (var comp in _components)
        {
            var fitness = await comp.EvaluateAsync(sp, ct);
            if (fitness.Score < 0.7 && experience.Patterns.Count > 0)
            {
                var topPattern = experience.Patterns.FirstOrDefault(p => p.Severity == "critical" || p.Severity == "high");
                if (topPattern != null)
                {
                    var edit = _decisionLog.RecordEdit(
                        new() { topPattern.Pattern },
                        topPattern.RootCause,
                        topPattern.SuggestedFix ?? "Auto-suggested fix",
                        comp.ComponentName,
                        before.Components.FirstOrDefault(c => c.Name == comp.ComponentName)?.Hash ?? "",
                        comp.CurrentHash,
                        $"Expected to fix {topPattern.OccurrenceCount} occurrences of '{topPattern.Pattern}'",
                        0.1);

                    await comp.ApplyEditAsync(edit, sp, ct);
                    edits.Add(edit);
                }
            }
        }

        var after = _snapshot.Capture();
        var changes = _snapshot.Diff(before, after);

        return new EvolutionIterationResult
        {
            ComponentCount = _components.Count,
            EditsApplied = edits.Count,
            Changes = changes,
            BeforeHash = before.Components.FirstOrDefault()?.Hash ?? "",
            AfterHash = after.Components.FirstOrDefault()?.Hash ?? ""
        };
    }

    public async Task VerifyPendingEditsAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        foreach (var edit in _decisionLog.GetPendingEdits())
        {
            var component = _components.FirstOrDefault(c => c.ComponentName == edit.Component);
            if (component == null) continue;

            var fitness = await component.EvaluateAsync(sp, ct);
            var improved = fitness.Score > 0.7;

            _decisionLog.VerifyEdit(edit.Id, improved,
                improved ? $"Component {edit.Component} score: {fitness.Score:F2}" : $"Score {fitness.Score:F2} below threshold",
                fitness.Score);
        }
    }
}

public sealed class EvolutionIterationResult
{
    public int ComponentCount { get; init; }
    public int EditsApplied { get; init; }
    public List<string> Changes { get; init; } = new();
    public string BeforeHash { get; init; } = "";
    public string AfterHash { get; init; } = "";
}
