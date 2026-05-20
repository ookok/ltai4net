using System.Diagnostics;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public sealed record RefinementStep
{
    public int Round { get; init; }
    public string Generation { get; init; } = "";
    public string Verification { get; init; } = "";
    public string Critique { get; init; } = "";
    public bool Accepted { get; init; }
    public List<Issue> Issues { get; init; } = new();
    public double QualityScore { get; init; }
    public int TokenCount { get; init; }
    public long DurationMs { get; init; }
}

public sealed record Issue
{
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public IssueSeverity Severity { get; init; } = IssueSeverity.Minor;
}

public enum IssueSeverity { Critical, Major, Minor, Suggestion }

public sealed record SelfRefineResult
{
    public string FinalAnswer { get; init; } = "";
    public List<RefinementStep> RefinementHistory { get; init; } = new();
    public bool Accepted { get; init; }
    public int TotalRounds { get; init; }
    public int TotalTokens { get; init; }
    public long TotalDurationMs { get; init; }
    public List<string> KnowledgeSources { get; init; } = new();
}

public sealed class SelfRefinementLoop
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<SelfRefinementLoop>? _logger;

    public SelfRefinementLoop(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        PromptBuilder promptBuilder,
        ILogger<SelfRefinementLoop>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public async Task<SelfRefineResult> SolveAsync(
        string problem,
        RefinementConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new RefinementConfig();
        var sw = Stopwatch.StartNew();
        var history = new List<RefinementStep>();
        var sources = new List<string>();
        int totalTokens = 0;

        var currentSolution = await GenerateAsync(problem, sources, cfg);
        totalTokens += EstimateTokens(currentSolution);

        for (int round = 0; round < cfg.MaxRefinementRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepSw = Stopwatch.StartNew();

            var (verification, issues, qualityScore) = await VerifyAsync(
                problem, currentSolution, round, cfg);

            var step = new RefinementStep
            {
                Round = round + 1,
                Generation = currentSolution,
                Verification = verification,
                Critique = BuildCritiqueSummary(issues),
                Issues = issues,
                QualityScore = qualityScore,
                TokenCount = EstimateTokens(currentSolution + verification),
                DurationMs = stepSw.ElapsedMilliseconds,
                Accepted = qualityScore >= cfg.AcceptanceThreshold && !issues.Any(i => i.Severity == IssueSeverity.Critical)
            };

            history.Add(step);
            totalTokens += step.TokenCount;

            if (step.Accepted)
            {
                _logger?.LogInformation(
                    "SelfRefine: accepted at round {Round} quality={Quality:F2} tokens={Tokens}",
                    round + 1, qualityScore, totalTokens);
                break;
            }

            if (cfg.RecordRejected && qualityScore < cfg.AcceptanceThreshold * 0.5)
            {
                _logger?.LogWarning(
                    "SelfRefine: low quality round {Round} score={Quality:F2} issues={IssueCount}",
                    round + 1, qualityScore, issues.Count);
            }

            if (round >= cfg.MaxRefinementRounds - 1)
            {
                step = step with { Accepted = false };
                break;
            }

            currentSolution = await RefineAsync(problem, currentSolution, verification, issues, sources, cfg);
            totalTokens += EstimateTokens(currentSolution);
        }

        sw.Stop();
        var lastStep = history.LastOrDefault();

        return new SelfRefineResult
        {
            FinalAnswer = lastStep?.Generation ?? currentSolution,
            RefinementHistory = history,
            Accepted = lastStep?.Accepted ?? false,
            TotalRounds = history.Count,
            TotalTokens = totalTokens,
            TotalDurationMs = sw.ElapsedMilliseconds,
            KnowledgeSources = sources.Distinct().ToList()
        };
    }

    private async Task<string> GenerateAsync(
        string problem, List<string> sources, RefinementConfig cfg)
    {
        var docs = _agenticRAG.Search(problem, RAGMode.Iterative, maxRounds: 2, domain: "general");
        foreach (var d in docs) sources.Add(d.Title ?? "");

        var opts = new PromptBuildOptions
        {
            Domain = "proof_solving",
            MaxContextTokens = cfg.MaxContextTokens / 4,
            CitationStyle = CitationStyle.MarkdownRef
        };

        var systemPrompt = BuildGenerateSystemPrompt(cfg);
        var userPrompt = $"## Problem\n{problem}\n\nGenerate a complete solution with rigorous reasoning.";

        var prompt = _promptBuilder.BuildSinglePrompt(userPrompt, docs, opts);
        var fullPrompt = systemPrompt + "\n\n" + prompt;

        var response = await _chatClient.GetResponseAsync(fullPrompt, cancellationToken: default);
        return response.Text ?? "";
    }

    private async Task<(string verification, List<Issue> issues, double qualityScore)> VerifyAsync(
        string problem, string solution, int round, RefinementConfig cfg)
    {
        var verifyPrompt = BuildVerifyPrompt(problem, solution);
        var response = await _chatClient.GetResponseAsync(verifyPrompt);

        var raw = response.Text ?? "";
        var issues = ParseIssues(raw);
        var qualityScore = ComputeQualityScore(issues, solution.Length, cfg);

        return (raw, issues, qualityScore);
    }

    private async Task<string> RefineAsync(
        string problem, string currentSolution, string verification,
        List<Issue> issues, List<string> sources, RefinementConfig cfg)
    {
        var docs = _agenticRAG.Search(problem, RAGMode.Iterative, maxRounds: 1, domain: "general");
        foreach (var d in docs) sources.Add(d.Title ?? "");

        var refinePrompt = BuildRefinePrompt(problem, currentSolution, verification, issues);
        var response = await _chatClient.GetResponseAsync(refinePrompt);
        return response.Text ?? currentSolution;
    }

    private static string BuildGenerateSystemPrompt(RefinementConfig cfg)
    {
        return $"""
            You are an olympiad-level mathematics and science problem solver.

            Requirements:
            1. Provide rigorous step-by-step reasoning with clear logical flow
            2. State definitions, lemmas, and theorems explicitly before using them
            3. Include self-checks at critical junctures
            4. Verify the final answer satisfies all constraints
            5. Aim for solution length between {cfg.MinSolutionTokens * 4} and {cfg.MaxSolutionTokens * 4} characters
            """;
    }

    private static string BuildVerifyPrompt(string problem, string solution)
    {
        return $"""
            You are a rigorous proof validator. Review the following solution for correctness.

            Problem:
            {problem}

            Solution to verify:
            {solution[..Math.Min(solution.Length, 12000)]}

            Provide a structured verification:

            ## Overall Assessment
            [Pass/Fail] with explanation

            ## Issues Found
            For each issue, use the format:
            - [CRITICAL/MAJOR/MINOR/SUGGESTION] <category>: <description> (location: <section/step>)

            Categories: logical_gap, calculation_error, missing_case, assumption_invalid, notation_error, reasoning_incomplete, conclusion_mismatch

            ## Correctness Score (0.0-1.0)
            [score]
            """;
    }

    private static string BuildRefinePrompt(
        string problem, string solution, string verification, List<Issue> issues)
    {
        var issueList = string.Join("\n", issues.Select(i =>
            $"  - [{i.Severity.ToString().ToUpper()}] {i.Category}: {i.Description}"));

        return $"""
            You are refining a solution based on verification feedback.

            ## Original Problem
            {problem}

            ## Current Solution
            {solution[..Math.Min(solution.Length, 8000)]}

            ## Verification Feedback
            {verification[..Math.Min(verification.Length, 4000)]}

            ## Issues to Fix
            {issueList}

            ## Instructions
            Rewrite the solution addressing all CRITICAL and MAJOR issues.
            Preserve correct parts of the original solution.
            Add explicit verification steps after key derivations.
            Ensure the final answer is clearly stated and boxed.

            ## Refined Solution
            """;
    }

    private static List<Issue> ParseIssues(string verification)
    {
        var issues = new List<Issue>();
        var pattern = @"-\s*\[(CRITICAL|MAJOR|MINOR|SUGGESTION)\]\s*(\w+):\s*(.+?)(?:\s*\(location:\s*(.+?)\))?\s*$";

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(
            verification, pattern, System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            var m = (System.Text.RegularExpressions.Match)match;
            if (!m.Success) continue;

            var severity = m.Groups[1].Value.ToUpperInvariant() switch
            {
                "CRITICAL" => IssueSeverity.Critical,
                "MAJOR" => IssueSeverity.Major,
                "SUGGESTION" => IssueSeverity.Suggestion,
                _ => IssueSeverity.Minor
            };

            issues.Add(new Issue
            {
                Severity = severity,
                Category = m.Groups[2].Value.Trim(),
                Description = m.Groups[3].Value.Trim(),
                Location = m.Groups[4].Success ? m.Groups[4].Value.Trim() : ""
            });
        }

        if (issues.Count == 0 && verification.Length > 0)
        {
            var lower = verification.ToLowerInvariant();
            if (lower.Contains("no issue") || lower.Contains("correct") || lower.Contains("pass"))
                return issues;

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Minor,
                Category = "unstructured_feedback",
                Description = verification[..Math.Min(200, verification.Length)].Trim()
            });
        }

        return issues;
    }

    private static double ComputeQualityScore(
        List<Issue> issues, int solutionLength, RefinementConfig cfg)
    {
        var baseScore = 1.0;

        foreach (var issue in issues)
        {
            baseScore -= issue.Severity switch
            {
                IssueSeverity.Critical => 0.25,
                IssueSeverity.Major => 0.12,
                IssueSeverity.Minor => 0.05,
                IssueSeverity.Suggestion => 0.02,
                _ => 0
            };
        }

        var minTokens = cfg.MinSolutionTokens * 4;
        var maxTokens = cfg.MaxSolutionTokens * 4;
        if (solutionLength < minTokens)
            baseScore -= 0.1 * (1.0 - (double)solutionLength / minTokens);
        if (solutionLength > maxTokens)
            baseScore -= 0.05;

        return Math.Max(0, Math.Min(1.0, baseScore));
    }

    private static string BuildCritiqueSummary(List<Issue> issues)
    {
        if (issues.Count == 0) return "No issues found.";

        var bySeverity = issues.GroupBy(i => i.Severity)
            .ToDictionary(g => g.Key, g => g.Count());

        var parts = new List<string>();
        if (bySeverity.TryGetValue(IssueSeverity.Critical, out var c) && c > 0)
            parts.Add($"{c} critical");
        if (bySeverity.TryGetValue(IssueSeverity.Major, out var mj) && mj > 0)
            parts.Add($"{mj} major");
        if (bySeverity.TryGetValue(IssueSeverity.Minor, out var mn) && mn > 0)
            parts.Add($"{mn} minor");
        if (bySeverity.TryGetValue(IssueSeverity.Suggestion, out var s) && s > 0)
            parts.Add($"{s} suggestions");

        return $"Found {issues.Count} issues ({string.Join(", ", parts)}).";
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);
}

public sealed class RefinementConfig
{
    public int MaxRefinementRounds { get; set; } = 5;
    public double AcceptanceThreshold { get; set; } = 0.85;
    public int MaxContextTokens { get; set; } = 32000;
    public int MinSolutionTokens { get; set; } = 500;
    public int MaxSolutionTokens { get; set; } = 25000;
    public bool RecordRejected { get; set; } = true;
}
