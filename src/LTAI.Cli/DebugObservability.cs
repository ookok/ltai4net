using LTAI.AI.Governors;
using LTAI.AI.Interfaces;
using LTAI.Cli.Debug;

namespace LTAI.Cli;

public sealed class DebugObservability
{
    private readonly ILivingTreeSystem _lts;
    private readonly FullLinkTracer _tracer;

    public DebugObservability(ILivingTreeSystem lts)
    {
        _lts = lts;
        _tracer = new FullLinkTracer();
    }

    public FullLinkTracer Tracer => _tracer;

    public Dictionary<string, object> Snapshot()
    {
#pragma warning disable CS8601
        var snap = new Dictionary<string, object>();

        try
        {
            snap["system.mode"] = _lts.Mode.ToString();
            snap["system.l1_model"] = _lts.GetType().GetProperty("FlashModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts)?.ToString() ?? "?";
            snap["system.l2_model"] = _lts.GetType().GetProperty("DefaultModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_lts)?.ToString() ?? "?";
            snap["system.dna_enabled"] = _lts.DNAEnabled;
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var bavt = _lts.GetType().GetField("_bavtRouter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts);
            if (bavt != null)
            {
                snap["bavt.budget_ratio"] = bavt.GetType().GetProperty("BudgetRatio")?.GetValue(bavt);
                snap["bavt.remaining"] = bavt.GetType().GetProperty("RemainingBudget")?.GetValue(bavt);
                snap["bavt.total_spent"] = bavt.GetType().GetProperty("TotalSpent")?.GetValue(bavt);
            }
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var erl = _lts.GetType().GetField("_erlLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts);
            if (erl != null)
            {
                snap["erl.total_trials"] = erl.GetType().GetProperty("TotalTrials")?.GetValue(erl);
                snap["erl.success_rate"] = erl.GetType().GetProperty("SuccessRate")?.GetValue(erl);
            }
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var elastic = _lts.GetType().GetField("_elasticMemory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts);
            if (elastic != null)
            {
                var stats = elastic.GetType().GetProperty("Stats")?.GetValue(elastic);
                if (stats != null)
                {
                    var type = stats.GetType();
                    snap["elastic.raw"] = type.GetField("Item1")?.GetValue(stats);
                    snap["elastic.compressed"] = type.GetField("Item2")?.GetValue(stats);
                    snap["elastic.episodic"] = type.GetField("Item3")?.GetValue(stats);
                }
            }
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var reflection = _lts.GetType().GetField("_reflectionEngine",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts);
            if (reflection != null)
            {
                snap["reflection.total"] = reflection.GetType().GetProperty("TotalReflections")?.GetValue(reflection);
                snap["reflection.recovery_rate"] = reflection.GetType().GetProperty("RecoveryRate")?.GetValue(reflection);
            }
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var evolution = _lts.GetType().GetField("_evolutionStore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts) as ICrossRunEvolutionStore;
            if (evolution != null)
            {
                snap["evolution.total_lessons"] = evolution.LessonCount;
                snap["evolution.active_lessons"] = evolution.ActiveLessonCount;
                snap["evolution.lessons_prompt"] = evolution.FormatLessonsAsPrompt(3);
            }
        }
        catch { /* intentional: cleanup may fail */ }

        try
        {
            var verifiable = _lts.GetType().GetField("_verifiableRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_lts) as IVerifiableRegistry;
            if (verifiable != null)
            {
                snap["verifiable.measurements"] = verifiable.MeasurementCount;
                snap["verifiable.citations"] = verifiable.VerifiedCitationCount;
            }
        }
        catch { /* intentional: cleanup may fail */ }

#pragma warning restore CS8601
        return snap;
    }
}
