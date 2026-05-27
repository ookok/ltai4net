using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Governors;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Review;

public sealed class CodeReviewEngine
{
    public static IMicroKernel? Kernel { get; set; }
    private readonly ILogger<CodeReviewEngine> _logger;
    private readonly MultiLangCodeAnalyzer _analyzer;

    public CodeReviewEngine(ILogger<CodeReviewEngine> logger, MultiLangCodeAnalyzer analyzer)
    {
        _logger = logger;
        _analyzer = analyzer;
    }

    public async Task<ReviewReport> ReviewAsync(
        string? target = null,
        ReviewScope scope = ReviewScope.Staged,
        CancellationToken cancellationToken = default)
    {
        var changes = await GetChangesAsync(target, scope, cancellationToken).ConfigureAwait(false);
        if (changes.Count == 0)
            return new ReviewReport { Summary = "No changes to review." };

        var issues = new List<ReviewIssue>();
        foreach (var change in changes)
        {
            var fileIssues = await ReviewFileAsync(change, cancellationToken).ConfigureAwait(false);
            issues.AddRange(fileIssues);
        }

        var report = GenerateReport(changes, issues);
        _logger.LogInformation("Code review: {Files} files, {Issues} issues, score={Score}",
            changes.Count, issues.Count, report.OverallScore);

        return report;
    }

    private async Task<List<FileChange>> GetChangesAsync(string? target, ReviewScope scope, CancellationToken ct)
    {
        var changes = new List<FileChange>();
        string? diffOutput = null;

        try
        {
            diffOutput = scope switch
            {
                ReviewScope.Staged => RunGit("diff --cached --unified=3"),
                ReviewScope.Unstaged => RunGit("diff --unified=3"),
                ReviewScope.Branch when target != null => RunGit($"diff {target}...HEAD --unified=3"),
                ReviewScope.File when target != null && File.Exists(target) => File.ReadAllText(target),
                _ => RunGit("diff --unified=3")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git diff failed");
        }

        if (string.IsNullOrWhiteSpace(diffOutput)) return changes;

        var files = Regex.Split(diffOutput, @"^diff --git ", RegexOptions.Multiline)
            .Where(s => !string.IsNullOrWhiteSpace(s));

        foreach (var fileDiff in files)
        {
            var header = Regex.Match(fileDiff, @"a/(.+?) b/(.+?)\n");
            if (!header.Success) continue;

            var fileName = header.Groups[2].Value;
            var hunks = ParseHunks(fileDiff);

            if (hunks.Count > 0)
            {
                changes.Add(new FileChange
                {
                    FileName = fileName,
                    Language = LanguageRegistry.Detect(fileName),
                    Hunks = hunks,
                    AddedLines = hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Added)),
                    RemovedLines = hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Removed))
                });
            }
        }

        return await Task.FromResult(changes).ConfigureAwait(false);
    }

    private static List<DiffHunk> ParseHunks(string fileDiff)
    {
        var hunks = new List<DiffHunk>();
        var hunkMatches = Regex.Matches(fileDiff, @"^@@ -(\d+),?\d* \+(\d+),?\d* @@\s*(.*)", RegexOptions.Multiline);

        foreach (Match hm in hunkMatches)
        {
            var hunk = new DiffHunk
            {
                OldStart = int.Parse(hm.Groups[1].Value),
                NewStart = int.Parse(hm.Groups[2].Value),
                Context = hm.Groups[3].Value.Trim()
            };

            var startIdx = hm.Index + hm.Length;
            var nextIdx = hm.NextMatch().Success ? hm.NextMatch().Index : fileDiff.Length;
            var hunkContent = fileDiff[startIdx..nextIdx];

            foreach (var line in hunkContent.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                hunk.Lines.Add(new DiffLine
                {
                    Type = line[0] switch { '+' => DiffLineType.Added, '-' => DiffLineType.Removed, _ => DiffLineType.Context },
                    Content = line.Length > 1 ? line[1..] : "",
                    LineNumber = line[0] == '+' ? hunk.NewStart + hunk.Lines.Count(l => l.Type != DiffLineType.Removed)
                               : line[0] == '-' ? hunk.OldStart + hunk.Lines.Count(l => l.Type == DiffLineType.Removed) : 0
                });
            }

            hunks.Add(hunk);
        }

        return hunks;
    }

    private async Task<List<ReviewIssue>> ReviewFileAsync(FileChange change, CancellationToken ct)
    {
        var issues = new List<ReviewIssue>();
        var lang = change.Language;

        foreach (var hunk in change.Hunks)
        {
            foreach (var line in hunk.Lines.Where(l => l.Type == DiffLineType.Added))
            {
                CheckSecurity(line, change.FileName, lang, issues);
                CheckPatterns(line, change.FileName, lang, issues);
                CheckStyle(line, change.FileName, lang, issues);
            }

            var addedLines = hunk.Lines.Where(l => l.Type == DiffLineType.Added).Select(l => l.Content).ToList();
            var joined = string.Join("\n", addedLines);

            if (addedLines.Count > 20)
            {
                issues.Add(new ReviewIssue
                {
                    File = change.FileName,
                    Line = hunk.NewStart,
                    Severity = IssueSeverity.Info,
                    Category = "complexity",
                    Title = "Large change block",
                    Message = $"Hunk adds {addedLines.Count} lines. Consider splitting into smaller changes.",
                    Suggestion = "Break large hunks into focused, single-purpose changes."
                });
            }
        }

        return await Task.FromResult(issues).ConfigureAwait(false);
    }

    private void CheckSecurity(DiffLine line, string file, CodeLanguage lang, List<ReviewIssue> issues)
    {
        var content = line.Content;
        var lineNum = line.LineNumber;

        var checks = new (string pattern, string title, string msg, IssueSeverity sev)[]
        {
            (@"\.(exec|eval|spawn)\s*\(", "Dangerous function call", "eval/exec/spawn detected", IssueSeverity.Critical),
            (@"password\s*=\s*['""][^'""]{4,}['""]", "Hardcoded password", "Password embedded in code", IssueSeverity.Critical),
            (@"api[_\s]?key\s*=\s*['""][^'""]{8,}['""]", "Hardcoded API key", "API key exposed in source", IssueSeverity.Critical),
            (@"TODO|FIXME|HACK", "Incomplete code marker", "TODO/FIXME/HACK marker found", IssueSeverity.Info),
            (@"Debug\.Assert|Console\.WriteLine|print\(|console\.log", "Debug output", "Debug statement in production code", IssueSeverity.Warning),
            (@"Thread\.Sleep\(", "Blocking sleep", "Thread.Sleep blocks thread", IssueSeverity.Warning),
            (@"SELECT\s+\*\s+FROM", "SELECT *", "Avoid SELECT *, list columns explicitly", IssueSeverity.Warning),
            (@"catch\s*\(\s*Exception\s*\)", "Catch-all exception", "Avoid catching generic Exception", IssueSeverity.Warning),
            (@"catch\s*\{\s*\}", "Empty catch block", "Empty catch swallows exceptions", IssueSeverity.Warning),
            (@"new\s+Random\s*\(", "Non-crypto random", "Use RandomNumberGenerator for security-sensitive operations", IssueSeverity.Info),
            (@"public\s+static\s+(?!readonly)", "Mutable static field", "Static non-readonly fields are not thread-safe", IssueSeverity.Warning),
            (@"\.Result\b|\.Wait\s*\(\)", "Sync-over-async", "Blocking on async code causes deadlocks", IssueSeverity.Critical),
        };

        foreach (var (pattern, title, msg, severity) in checks)
        {
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
            {
                issues.Add(new ReviewIssue
                {
                    File = file, Line = lineNum, Severity = severity,
                    Category = "security", Title = title, Message = msg,
                    Code = content.Trim(),
                    Suggestion = $"Replace with safe alternative."
                });
            }
        }
    }

    private void CheckPatterns(DiffLine line, string file, CodeLanguage lang, List<ReviewIssue> issues)
    {
        var content = line.Content;

        if (content.Length > 150)
        {
            issues.Add(new ReviewIssue
            {
                File = file, Line = line.LineNumber, Severity = IssueSeverity.Info,
                Category = "style", Title = "Line too long",
                Message = $"Line exceeds 150 characters ({content.Length})",
                Suggestion = "Break into multiple lines."
            });
        }

        if (lang == CodeLanguage.Python && content.Contains('\t'))
        {
            issues.Add(new ReviewIssue
            {
                File = file, Line = line.LineNumber, Severity = IssueSeverity.Warning,
                Category = "style", Title = "Tab indentation",
                Message = "Python uses spaces, not tabs.",
                Suggestion = "Replace tabs with 4 spaces."
            });
        }
    }

    private void CheckStyle(DiffLine line, string file, CodeLanguage lang, List<ReviewIssue> issues)
    {
        var content = line.Content.Trim();

        if (content.EndsWith(" )") || content.EndsWith("( "))
        {
            issues.Add(new ReviewIssue
            {
                File = file, Line = line.LineNumber, Severity = IssueSeverity.Info,
                Category = "style", Title = "Trailing/malformed parentheses whitespace",
                Message = "Whitespace issue inside parentheses.",
                Suggestion = "Remove extra whitespace inside parentheses."
            });
        }
    }

    private ReviewReport GenerateReport(List<FileChange> changes, List<ReviewIssue> issues)
    {
        var critical = issues.Count(i => i.Severity == IssueSeverity.Critical);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        var infos = issues.Count(i => i.Severity == IssueSeverity.Info);

        var score = 100.0 - critical * 15 - warnings * 5 - infos * 1;
        score = Math.Max(0, Math.Min(100, score));

        return new ReviewReport
        {
            FilesChanged = changes.Count,
            TotalIssues = issues.Count,
            CriticalIssues = critical,
            Warnings = warnings,
            Infos = infos,
            OverallScore = score,
            Summary = score >= 90 ? "Excellent — ready to merge" :
                      score >= 70 ? "Good — minor issues to address" :
                      score >= 50 ? "Needs work — address warnings" :
                      "Blocked — fix critical issues before merge",
            Issues = issues,
            ChangedFiles = changes.Select(c => c.FileName).ToList()
        };
    }

    private static string RunGit(string arguments)
    {
        if (Kernel != null)
        {
            var result = Kernel.GitOpAsync("diff", arguments, CancellationToken.None).GetAwaiter().GetResult();
            if (result.Success) return result.Data ?? "";
        }

        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }
}

public enum ReviewScope { Staged, Unstaged, Branch, File }

public enum IssueSeverity { Info, Warning, Critical }

public sealed class FileChange
{
    public string FileName { get; init; } = "";
    public CodeLanguage Language { get; init; }
    public List<DiffHunk> Hunks { get; init; } = new();
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
}

public sealed class DiffHunk
{
    public int OldStart { get; init; }
    public int NewStart { get; init; }
    public string Context { get; init; } = "";
    public List<DiffLine> Lines { get; init; } = new();
}

public sealed class DiffLine
{
    public DiffLineType Type { get; init; }
    public string Content { get; init; } = "";
    public int LineNumber { get; init; }
}

public enum DiffLineType { Context, Added, Removed }

public sealed class ReviewIssue
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public IssueSeverity Severity { get; init; }
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Code { get; init; } = "";
    public string Suggestion { get; init; } = "";
}

public sealed class ReviewReport
{
    public int FilesChanged { get; init; }
    public int TotalIssues { get; init; }
    public int CriticalIssues { get; init; }
    public int Warnings { get; init; }
    public int Infos { get; init; }
    public double OverallScore { get; init; }
    public string Summary { get; init; } = "";
    public List<ReviewIssue> Issues { get; init; } = new();
    public List<string> ChangedFiles { get; init; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Code Review Report");
        sb.AppendLine();
        sb.AppendLine($"**Score:** {OverallScore:F0}/100 — {Summary}");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Files changed | {FilesChanged} |");
        sb.AppendLine($"| Total issues | {TotalIssues} |");
        sb.AppendLine($"| 🔴 Critical | {CriticalIssues} |");
        sb.AppendLine($"| 🟡 Warnings | {Warnings} |");
        sb.AppendLine($"| 🔵 Info | {Infos} |");
        sb.AppendLine();

        sb.AppendLine("## Files");
        foreach (var f in ChangedFiles)
            sb.AppendLine($"- `{f}`");
        sb.AppendLine();

        if (Issues.Count > 0)
        {
            sb.AppendLine("## Issues");
            foreach (var g in Issues.GroupBy(i => i.Severity).OrderByDescending(g => g.Key))
            {
                var emoji = g.Key switch { IssueSeverity.Critical => "🔴", IssueSeverity.Warning => "🟡", _ => "🔵" };
                sb.AppendLine($"### {emoji} {g.Key}");
                foreach (var issue in g)
                {
                    sb.AppendLine($"- **{issue.Title}** — `{issue.File}:{issue.Line}`");
                    sb.AppendLine($"  {issue.Message}");
                    if (!string.IsNullOrEmpty(issue.Suggestion))
                        sb.AppendLine($"  > Suggestion: {issue.Suggestion}");
                    if (!string.IsNullOrEmpty(issue.Code))
                        sb.AppendLine($"  ```\n  {issue.Code}\n  ```");
                }
            }
        }

        return sb.ToString();
    }
}
