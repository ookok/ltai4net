using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LTAI.Hpo;

namespace LTAI.Hpo.Dashboard;

/// <summary>
/// Renders HPO dashboard data as text blocks for TUI/CLI/Desktop DevUI.
/// </summary>
public static class HpoDashboardRenderer
{
    /// <summary>Render a summary table of all studies.</summary>
    public static string RenderStudiesSummary(IReadOnlyDictionary<string, Study> studies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║              HPO \u8d85\u53c2\u6570\u4f18\u5316\u4eea\u8868\u76d8                      ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();

        if (studies.Count == 0)
        {
            sb.AppendLine("\u6682\u65e0\u6d3b\u8dc3\u7684\u4f18\u5316\u4efb\u52a1\u3002");
            return sb.ToString();
        }

        foreach (var (name, study) in studies)
        {
            sb.AppendLine("[HPO] " + name);
            sb.AppendLine("  Direction:  " + study.Direction);
            sb.AppendLine("  Sampler:    " + study.Sampler.GetType().Name);
            sb.AppendLine("  Trials:     " + study.CompletedCount + " completed");
            var bv = study.BestValue?.ToString("F6") ?? "\u2014";
            sb.AppendLine("  Best value: " + bv);
            sb.AppendLine("  Elapsed:    " + study.Elapsed.TotalSeconds.ToString("F1") + "s");

            if (study.BestParams is { Count: > 0 })
            {
                sb.AppendLine("  Best params:");
                foreach (var (k, v) in study.BestParams)
                    sb.AppendLine("    " + k + " = " + v);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Render recent trials as a table.</summary>
    public static string RenderTrialsTable(IReadOnlyList<TrialRecord> trials)
    {
        var sb = new StringBuilder();
        sb.Append("| #  | State     | Value      | Params");
        sb.AppendLine();
        sb.Append("| ---| ----------| -----------| -----");
        sb.AppendLine();

        foreach (var t in trials.Take(20))
        {
            var st = t.State.ToString();
            if (t.State == TrialState.Completed) st = "C " + st;
            else if (t.State == TrialState.Pruned) st = "P " + st;
            else if (t.State == TrialState.Failed) st = "F " + st;
            else if (t.State == TrialState.Running) st = "R " + st;
            var val = t.Value?.ToString("F6") ?? "\u2014";
            var pars = HpoDashboard.FormatParams(t.Params);
            if (pars.Length > 60) pars = pars[..57] + "...";
            sb.AppendLine("| " + t.Number.ToString().PadLeft(3) + " | " + st.PadRight(10) + "| " + val.PadLeft(10) + " | " + pars);
        }

        return sb.ToString();
    }
}
