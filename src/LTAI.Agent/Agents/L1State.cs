using System.Text;
using LTAI.Agent.FusionRoute;

namespace LTAI.Agent;

/// <summary>
/// GoS-inspired structured belief state for L1 (flash model) reasoning.
/// Captures the exploration state — candidates, confidence gap, evidence,
/// and FusionRoute-inspired span-level uncertainty — so L2 (pro model)
/// can pick up exactly where L1 left off instead of starting over.
/// </summary>
public sealed class L1State
{
    /// <summary>Candidate hypotheses/entities with confidence scores.</summary>
    public List<(string name, string kind, double score)> Candidates { get; set; } = [];

    /// <summary>Confidence gap between top-1 and top-2 (like GoS gap_delta).</summary>
    public double Gap { get; set; }

    /// <summary>Number of supporting evidence edges found.</summary>
    public int SupportCount { get; set; }

    /// <summary>Steps/reasoning rounds spent by L1.</summary>
    public int StepsTaken { get; set; }

    /// <summary>Frozen state snapshot: "active" | "report" | "escalate".</summary>
    public string? Label { get; set; }

    /// <summary>Original L1 response (for backward compat when needed).</summary>
    public string? L1Response { get; set; }

    /// <summary>Reason L1 escalated to L2.</summary>
    public string? EscalationReason { get; set; }

    /// <summary>Tool calls made by L1 (names only).</summary>
    public List<string> ToolCalls { get; set; } = [];

    // ── FusionRoute: span-level uncertainty ──

    /// <summary>Parse spans from L1 response with per-span uncertainty scores.</summary>
    public List<ResponseSpanRouter.L1Span> Spans { get; set; } = [];

    /// <summary>Ratio of uncertain spans to total spans (0.0–1.0).</summary>
    public double SpanUncertaintyRatio { get; set; }

    /// <summary>Whether span-level routing should be attempted.</summary>
    public bool ShouldRouteBySpans =>
        Spans.Count > 0 && SpanUncertaintyRatio > 0 && SpanUncertaintyRatio < 0.8;

    public bool ShouldEscalate =>
        Label == "escalate" ||
        (Candidates.Count >= 2 && Gap < 0.3 && SupportCount < 2) ||
        StepsTaken >= 3;

    /// <summary>
    /// Serialize to TOON for structured L1→L2 handoff.
    /// </summary>
    public string ToHandoff(Formats.ResultFormat format = Formats.ResultFormat.Toon)
    {
        if (format == Formats.ResultFormat.Toon)
            return ToToonHandoff();
        return ToMarkdownHandoff();
    }

    /// <summary>Build FusionRoute-inspired span-routing handoff for L2.</summary>
    public string ToSpanRoutingHandoff(string originalQuery)
    {
        var router = new ResponseSpanRouter();
        return router.BuildBatchRefinePrompt(originalQuery, Spans.Where(s => s.UncertaintyScore >= 0.4).ToList(), L1Response ?? "");
    }

    private string ToMarkdownHandoff()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## L1 Exploration State");
        sb.AppendLine($"- Gap: {Gap:F2}, Support: {SupportCount}, Steps: {StepsTaken}");
        if (!string.IsNullOrEmpty(EscalationReason))
            sb.AppendLine($"- Escalation: {EscalationReason}");
        sb.AppendLine();
        if (Candidates.Count > 0)
        {
            sb.AppendLine("### Candidates");
            foreach (var (name, kind, score) in Candidates)
                sb.AppendLine($"- [{kind}] {name} (score={score:F2})");
        }
        sb.AppendLine();
        if (Spans.Count > 0)
        {
            sb.AppendLine("### Span Uncertainty");
            sb.AppendLine($"- Ratio: {SpanUncertaintyRatio:F2}");
            foreach (var span in Spans.Where(s => s.UncertaintyScore >= 0.4).Take(5))
                sb.AppendLine($"- [{span.UncertaintyScore:F2}] {span.Text[..Math.Min(span.Text.Length, 60)]}");
        }
        return sb.ToString();
    }

    private string ToToonHandoff()
    {
        var tw = new Formats.ToonWriter();
        tw.Comment("L1 exploration state — structured handoff for L2");
        tw.KeyValue("gap", Gap);
        tw.KeyValue("support", SupportCount);
        tw.KeyValue("steps", StepsTaken);
        if (!string.IsNullOrEmpty(EscalationReason))
            tw.KeyValue("escalation_reason", EscalationReason!);

        if (Candidates.Count > 0)
        {
            var cols = new[] { "name", "kind", "score" };
            var rows = Candidates.Select(c => (IReadOnlyList<string>)new[] {
                c.name, c.kind, c.score.ToString("F2")
            }).ToList();
            tw.Table("candidates", cols, rows);
        }

        if (ToolCalls.Count > 0)
        {
            tw.KeyValue("tool_calls", string.Join(", ", ToolCalls));
        }

        if (Spans.Count > 0)
        {
            tw.KeyValue("span_uncertainty_ratio", SpanUncertaintyRatio);
            var spanCols = new[] { "text", "uncertainty" };
            var spanRows = Spans.Where(s => s.UncertaintyScore >= 0.3)
                .Select(s => (IReadOnlyList<string>)new[] {
                    s.Text.Length > 60 ? s.Text[..60] + "..." : s.Text,
                    s.UncertaintyScore.ToString("F2")
                }).ToList();
            if (spanRows.Count > 0)
                tw.Table("uncertain_spans", spanCols, spanRows);
        }

        return tw.ToString();
    }
}
