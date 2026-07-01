// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CriticRepairStep — DeNovoSWE-inspired critic-repair synthesis
//
//  After parallel quality checks (GrammarCheck, AntiPatternCheck,
//  QualityGate, DoDCheck), this step consolidates all failures into
//  structured, actionable repair hints. Implements the
//  "divide and conquer + critic-repair" philosophy from DeNovoSWE.
//
//  Reference: arXiv:2606.10728
// ═══════════════════════════════════════════════════════════════

using System.Text;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Synthesizes actionable critic-repair hints from all blocking quality failures.
/// Runs deterministically (no LLM call) — uses structured templates to map
/// quality dimensions to specific repair actions.
/// </summary>
public sealed class CriticRepairStep : IPipelineStep
{
    private readonly ILogger<CriticRepairStep>? _logger;
    private readonly IChatClient? _reviewer;

    public string Name => "CriticRepair";

    public CriticRepairStep(ILogger<CriticRepairStep>? logger = null, IChatClient? reviewer = null)
    {
        _logger = logger;
        _reviewer = reviewer;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var failures = CollectFailures(context);
        if (failures.Count == 0)
            return context;

        var repairState = GetOrCreateRepairState(context);
        repairState.AttemptCount++;

        var difficulty = CalculateDifficulty(failures, repairState.AttemptCount);
        repairState.LastDifficulty = difficulty;
        repairState.TotalFailures = failures.Count;

        var hints = SynthesizeRepairHints(failures, repairState, difficulty);
        repairState.LastHintsHash = ComputeSimpleHash(hints);

        // LLM reviewer: when deterministic hints are insufficient after repeated attempts
        if (_reviewer != null && difficulty >= 0.7 && repairState.AttemptCount >= 2)
        {
            try
            {
                var llmAdvice = await GenerateReviewAsync(failures, context, repairState.AttemptCount)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(llmAdvice))
                    hints += "\n" + llmAdvice;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "CriticRepair: LLM reviewer failed (non-fatal)");
            }
        }

        lock (context.MessagesLock)
        {
            context.Messages.Add(new ChatMessage(ChatRole.System, hints));
        }

        context.Set("CriticRepairState", repairState);

        _logger?.LogInformation(
            "CriticRepair: synthesized {Count} failure(s) into repair hints (difficulty={Difficulty:F2}, attempt={Attempt})",
            failures.Count, difficulty, repairState.AttemptCount);

        return context;
    }

    private async Task<string?> GenerateReviewAsync(
        List<CriticFailure> failures, MessageContext context, int attemptCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior code reviewer. A template-based critic identified these issues, but the agent failed to fix them after multiple attempts.");
        sb.AppendLine();
        sb.AppendLine("## Query");
        sb.AppendLine(context.Request.Length > 500 ? context.Request[..500] + "..." : context.Request);
        sb.AppendLine();
        sb.AppendLine("## Failures");
        foreach (var f in failures)
        {
            sb.AppendLine($"- [{f.Severity}] {f.Dimension}: {f.Description}");
            sb.AppendLine($"  Hint: {f.FixHint}");
        }
        sb.AppendLine();
        sb.AppendLine("## Previous Tool Calls");
        foreach (var (name, args, result) in context.ToolCalls.TakeLast(5))
        {
            var r = result.Length > 200 ? result[..200] + "..." : result;
            sb.AppendLine($"- {name}({args}) → {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Diagnose the root cause and suggest a concrete fix the agent can apply. Be specific. Max 3 sentences.");

        var response = await _reviewer!
            .GetResponseAsync([new ChatMessage(ChatRole.User, sb.ToString())], null, context.CancellationToken)
            .ConfigureAwait(false);
        var text = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return "\n## LLM Reviewer Analysis\n" + text;
    }

    // ── Failure Collection ──

    private static List<CriticFailure> CollectFailures(MessageContext context)
    {
        var failures = new List<CriticFailure>();

        if (context.GrammarCheckBlocked)
        {
            if (context.TryGet<object>("GrammarErrors", out var rawErrors) && rawErrors is System.Collections.IList list && list.Count > 0)
            {
                var details = new List<string>();
                foreach (var err in list)
                {
                    if (err is GrammarError ge)
                        details.Add($"  - {ge.File}:{ge.Line}:{ge.Column} [{ge.Code}] {ge.Message}");
                    else if (err is GrammarErrorModel gem)
                        details.Add($"  - {gem.File}:{gem.Line}:{gem.Column} [{gem.Code}] {gem.Message}");
                    else
                        details.Add($"  - {err}");
                }
                failures.Add(new CriticFailure(
                    Dimension: "Grammar",
                    Severity: FailureSeverity.Error,
                    Description: $"{list.Count} syntax/grammar errors detected",
                    Details: details.Take(10).ToList(),
                    FixHint: "Fix syntax errors: check missing semicolons, unmatched braces, undefined references."));
            }
            else
            {
                failures.Add(new CriticFailure(
                    Dimension: "Grammar",
                    Severity: FailureSeverity.Error,
                    Description: "Grammar check failed",
                    Details: [],
                    FixHint: "Run the build/lint command to see exact errors, then fix each one."));
            }
        }

        if (context.AntiPatternBlocked)
        {
            if (context.TryGet<List<AntiPattern>>("AntiPatterns", out var apPatterns) && apPatterns?.Count > 0)
            {
                var errors = apPatterns.Where(p => p.Severity == "error").ToList();
                failures.Add(new CriticFailure(
                    Dimension: "AntiPattern",
                    Severity: FailureSeverity.Error,
                    Description: $"{errors.Count} anti-pattern error(s) detected ({apPatterns.Count} total patterns)",
                    Details: errors.Select(e => $"  - [{e.Severity}] {e.Category}/{e.Pattern}: {e.Message} {e.File ?? ""}:{e.Line}").ToList(),
                    FixHint: "Remove hardcoded secrets, fix merge conflict markers, replace cliché patterns."));
            }
            else
            {
                failures.Add(new CriticFailure(
                    Dimension: "AntiPattern",
                    Severity: FailureSeverity.Error,
                    Description: "Anti-pattern check failed",
                    Details: [],
                    FixHint: "Check for hardcoded API keys, merge conflicts, or code smells."));
            }
        }

        if (context.QualityGateBlocked)
        {
            if (context.TryGet<Dictionary<string, double>>("QualityGateScores", out var scores) && scores?.Count > 0)
            {
                var lowDims = scores.Where(kv => kv.Value < 5.0).Select(kv => $"  - {kv.Key}: {kv.Value:F1}/10").ToList();
                failures.Add(new CriticFailure(
                    Dimension: "QualityGate",
                    Severity: FailureSeverity.Warning,
                    Description: $"Quality score below threshold (dimensions: {string.Join(", ", lowDims.Select(d => d.Trim()[..d.Trim().IndexOf(':')]))})",
                    Details: lowDims,
                    FixHint: "Improve response quality: add structure, remove hedge words, ensure completeness."));
            }
            else
            {
                failures.Add(new CriticFailure(
                    Dimension: "QualityGate",
                    Severity: FailureSeverity.Warning,
                    Description: "Quality gate not passed",
                    Details: [],
                    FixHint: "Enhance response structure, clarity, completeness, and craft quality."));
            }
        }

        if (context.DoDBlocked)
        {
            if (context.TryGet<List<string>>("DoDFailures", out var dodFailures) && dodFailures?.Count > 0)
            {
                failures.Add(new CriticFailure(
                    Dimension: "DoD",
                    Severity: FailureSeverity.Error,
                    Description: $"{dodFailures.Count} Definition of Done check(s) failed",
                    Details: dodFailures,
                    FixHint: "Ensure no TODO/FIXME, add tests, remove placeholders ({{ }}), confirm no syntax errors."));
            }
            else
            {
                failures.Add(new CriticFailure(
                    Dimension: "DoD",
                    Severity: FailureSeverity.Error,
                    Description: "Definition of Done check failed",
                    Details: [],
                    FixHint: "Verify completeness: no TODOs, tests present, documentation updated."));
            }
        }

        if (context.AbstentionBlocked)
        {
            var rules = new List<string>();
            if (context.TryGet<List<string>>("AbstentionRules", out var abstentionRules) && abstentionRules?.Count > 0)
                rules = abstentionRules;
            else
                rules = ["Agentic Abstention triggered"];

            failures.Add(new CriticFailure(
                Dimension: "Abstention",
                Severity: FailureSeverity.Warning,
                Description: "Agent detected a stopping condition",
                Details: rules,
                FixHint: "Review the stopping rules: if the task is genuinely impossible, provide a clear explanation of why; if the task is still viable, adjust the approach to avoid the detected pattern (repeated calls, empty results, etc.)."));
        }

        // Verbal-R3 Retrieval Quality check
        if (context.TryGet<VerbalAnnotationSet>("VerbalAnnotations", out var annSet) && annSet != null)
        {
            if (annSet.Annotations.Count > 0 && annSet.AverageConfidence < 0.4)
            {
                failures.Add(new CriticFailure(
                    Dimension: "RetrievalQuality",
                    Severity: FailureSeverity.Warning,
                    Description: $"Verbal-R3 检索置信度偏低 (avg={annSet.AverageConfidence:P1}, high={annSet.HighConfidenceRatio:P1})",
                    Details: annSet.Annotations
                        .Where(a => a.Confidence == AnnotationConfidence.Low)
                        .Select(a => $"  - [{a.SourceId}] {a.Rationale}")
                        .Take(5)
                        .ToList(),
                    FixHint: "降低 minSimilarity 阈值扩大搜索范围, 或使用更精确的查询词重新检索。Multiple low-confidence results suggest the retrieval scope is too narrow."));
            }

            // Persistently low confidence across multiple attempts
            if (context.TryGet<int>("RetrievalScalingRounds", out var scalingRounds) && scalingRounds >= 2)
            {
                failures.Add(new CriticFailure(
                    Dimension: "RetrievalQuality",
                    Severity: FailureSeverity.Warning,
                    Description: $"经过 {scalingRounds} 轮扩展检索后结果置信度仍不足",
                    Details: [$"  扩展轮次: {scalingRounds}"],
                    FixHint: "尝试更换检索策略: 使用不同的查询词、增加候选集大小、或改用语义搜索替代关键词搜索。"));
            }
        }

        return failures;
    }

    // ── Difficulty Calculation (DeNovoSWE-inspired) ──

    /// <summary>
    /// Calculate repair difficulty based on failure count, severity, and attempt history.
    /// Higher difficulty → more repair budget allocated.
    /// </summary>
    internal static double CalculateDifficulty(List<CriticFailure> failures, int attemptCount)
    {
        var errorCount = failures.Count(f => f.Severity == FailureSeverity.Error);
        var warningCount = failures.Count(f => f.Severity == FailureSeverity.Warning);

        // Base difficulty from severity-weighted failure count
        var baseDifficulty = (errorCount * 2.0 + warningCount * 0.5) / 3.0;

        // Cross-dimension penalty: failures in multiple dimensions are harder
        var dimensionCount = failures.Select(f => f.Dimension).Distinct().Count();
        var crossDimMultiplier = 1.0 + (dimensionCount - 1) * 0.3;

        // Attempt multiplier: repeated failures suggest harder problem
        var attemptMultiplier = 1.0 + (attemptCount - 1) * 0.15;

        return Math.Min(1.0, baseDifficulty * crossDimMultiplier * attemptMultiplier);
    }

    // ── Repair Hint Synthesis ──

    internal static string SynthesizeRepairHints(
        List<CriticFailure> failures, CriticRepairState repairState, double difficulty)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Critic-Repair Feedback (DeNovoSWE)");
        sb.AppendLine();
        sb.AppendLine($"**Difficulty**: {difficulty:F2} | **Attempt**: {repairState.AttemptCount}/{CriticRepairState.MaxRepairAttempts}");
        sb.AppendLine($"**Budget**: {(int)(CriticRepairState.MaxRepairAttempts * (1.0 - repairState.AttemptCount * 0.15))} remaining repair attempts");
        sb.AppendLine();

        // Overall strategy based on difficulty
        sb.AppendLine("### Repair Strategy");
        if (difficulty >= 0.8)
            sb.AppendLine("- **HIGH difficulty** — use divide-and-conquer: fix one dimension at a time, starting with the most severe.");
        else if (difficulty >= 0.5)
            sb.AppendLine("- **MEDIUM difficulty** — address errors first, then improve quality dimensions.");
        else
            sb.AppendLine("- **LOW difficulty** — quick targeted fixes should suffice.");
        sb.AppendLine();

        // Dimension-specific repair hints
        foreach (var failure in failures)
        {
            sb.AppendLine($"### {failure.Dimension} ({failure.Severity})");
            sb.AppendLine($"**Issue**: {failure.Description}");
            sb.AppendLine($"**Fix**: {failure.FixHint}");

            if (failure.Details.Count > 0)
            {
                sb.AppendLine("**Details**:");
                foreach (var detail in failure.Details)
                    sb.AppendLine(detail);
            }

            // Dimension-specific critic advice
            sb.AppendLine(GetDimensionSpecificAdvice(failure.Dimension, difficulty));
            sb.AppendLine();
        }

        // Anti-pattern persistence check
        if (repairState.LastHintsHash != null && repairState.AttemptCount > 1)
        {
            sb.AppendLine("> ⚠️ Same error pattern detected across retries. Try a fundamentally different approach:");
            sb.AppendLine("> - Re-examine tool arguments for correctness");
            sb.AppendLine("> - Consider using different tools or search strategies");
            sb.AppendLine("> - Break the task into smaller sub-tasks");
        }

        return sb.ToString();
    }

    private static string GetDimensionSpecificAdvice(string dimension, double difficulty)
    {
        return dimension switch
        {
            "Grammar" => difficulty >= 0.5
                ? "**Action**: Run build command to locate exact error lines. Fix top-down (first error often cascades)."
                : "**Action**: Quick syntax fix — check the specific error locations above.",

            "AntiPattern" => difficulty >= 0.5
                ? "**Action**: Remove all hardcoded secrets immediately. Use environment variables or config files. Strip merge conflict markers."
                : "**Action**: Remove the flagged anti-patterns listed above. Re-run the tool to verify.",

            "QualityGate" => difficulty >= 0.5
                ? "**Action**: Restructure response — add clear sections, reduce hedge words, ensure all query aspects are covered."
                : "**Action**: Polish output — improve clarity in the low-scoring dimensions shown above.",

            "DoD" => difficulty >= 0.5
                ? "**Action**: Review completeness — add missing tests, remove TODOs/FIXMEs, replace {{ }} placeholders, verify documentation."
                : "**Action**: Fill in the missing DoD criteria listed above. Quick completion check.",

            "RetrievalQuality" => difficulty >= 0.5
                ? "**Action**: Re-run retrieval with expanded scope. Use broader query keywords, reduce minSimilarity, invoke clustering for better coverage. Low Verbal-R3 confidence indicates the current search space is insufficient."
                : "**Action**: Adjust retrieval parameters — the verbal annotations show weak signal. Try alternative query formulations or fall back to BM25 FTS.",

            _ => "**Action**: Address the specific issues listed above. Verify fix with appropriate validation tool."
        };
    }

    // ── Repair State Management ──

    internal static CriticRepairState GetOrCreateRepairState(MessageContext context)
    {
        if (context.TryGet<CriticRepairState>("CriticRepairState", out var state) && state != null)
            return state;

        return new CriticRepairState();
    }

    private static int ComputeSimpleHash(string input)
    {
        var hash = 0;
        foreach (var c in input)
            hash = (hash * 31) + c;
        return hash;
    }

    // ── Simple grammar error model (avoids coupling to GrammarCheckStep.Models) ──

    internal sealed record GrammarErrorModel
    {
        public string File { get; init; } = "";
        public int Line { get; init; }
        public int Column { get; init; }
        public string Code { get; init; } = "";
        public string Message { get; init; } = "";
    }
}

// ── Supporting Types ──

internal sealed record CriticFailure(
    string Dimension,
    FailureSeverity Severity,
    string Description,
    List<string> Details,
    string FixHint);

internal enum FailureSeverity
{
    Error,
    Warning
}

/// <summary>State tracking for the critic-repair loop.</summary>
public sealed class CriticRepairState
{
    /// <summary>Maximum number of repair attempts before giving up.</summary>
    public const int MaxRepairAttempts = 3;

    /// <summary>Current repair attempt count (1-indexed).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Total number of failures across all dimensions.</summary>
    public int TotalFailures { get; set; }

    /// <summary>Last calculated difficulty score (0.0-1.0).</summary>
    public double LastDifficulty { get; set; }

    /// <summary>Hash of the last repair hints (for detecting persistent patterns).</summary>
    public int? LastHintsHash { get; set; }

    /// <summary>Per-dimension fix history for detecting stagnation.</summary>
    public Dictionary<string, int> DimensionAttempts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
