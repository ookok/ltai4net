using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools.Review;

/// <summary>
/// Post-processing module that validates and repairs file/line references in
/// review comments. Inspired by OCR's external positioning module.
/// </summary>
public sealed class ExternalPositioner
{
    // Matches file:line patterns like "src/foo.cs:42" or "path/to/file(line 10)"
    private static readonly Regex s_fileLineRef = new(
        @"(?:`?([\w/\\\.-]+\.(?:cs|cshtml|razor|xaml|js|ts|py|md|json|xml|yaml|yml))`?)(?:\s*[\(:]?\s*(?:line\s+)?(\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_markdownCodeBlock = new(
        @"```(\w*)\n(.+?)```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Parse file references from comment text and match against known diff files.
    /// Returns repaired comments with corrected positions.
    /// </summary>
    public List<RepairedComment> Repair(IReadOnlyList<ReviewComment> comments, List<DiffFileInfo> diffFiles)
    {
        var diffPaths = new HashSet<string>(diffFiles.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
        var results = new List<RepairedComment>();

        foreach (var comment in comments)
        {
            ReviewComment? repaired = null;
            var wasRepaired = false;
            var notes = new List<string>();

            // Check if referenced file is in diff
            if (!string.IsNullOrEmpty(comment.FilePath) && !diffPaths.Contains(comment.FilePath))
            {
                // Try fuzzy match: find closest matching file in diff
                var match = FindClosestFile(comment.FilePath, diffFiles);
                if (match != null)
                {
                    notes.Add($"File path corrected: '{comment.FilePath}' → '{match}'");
                    wasRepaired = true;
                    repaired = comment with { FilePath = match };
                }
                else
                {
                    notes.Add($"File '{comment.FilePath}' not found in diff — comment may be stale");
                }
            }

            // Validate line number
            if (comment.LineNumber > 0 && !string.IsNullOrEmpty(comment.FilePath))
            {
                var actualPath = repaired?.FilePath ?? comment.FilePath;
                if (File.Exists(actualPath))
                {
                    var lineCount = File.ReadLines(actualPath).Count();
                    if (comment.LineNumber > lineCount)
                    {
                        notes.Add($"Line {comment.LineNumber} exceeds file length ({lineCount} lines)");
                        wasRepaired = true;
                        repaired = (repaired ?? comment) with { LineNumber = Math.Min(comment.LineNumber, lineCount) };
                    }
                }
            }

            results.Add(new RepairedComment(
                Original: comment,
                Repaired: wasRepaired ? (repaired ?? comment) : null,
                WasRepaired: wasRepaired,
                RepairNote: notes.Count > 0 ? string.Join("; ", notes) : ""));
        }

        return results;
    }

    /// <summary>
    /// Extract file:line references from free-text review output.
    /// Useful for converting LLM-generated review text into structured comments.
    /// </summary>
    public List<(string filePath, int lineNumber, string context)> ExtractReferences(string reviewText)
    {
        var refs = new List<(string, int, string)>();
        var matches = s_fileLineRef.Matches(reviewText);

        foreach (Match m in matches)
        {
            var file = m.Groups[1].Value;
            var line = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (!string.IsNullOrEmpty(file))
                refs.Add((file, line, ""));
        }

        return refs;
    }

    /// <summary>
    /// Parse LLM review output into structured ReviewComment list.
    /// </summary>
    public List<ReviewComment> ParseStructuredComments(string reviewText)
    {
        var comments = new List<ReviewComment>();

        // Try to extract P0/P1/P2 sections
        var sections = Regex.Split(reviewText, @"(?=###\s*(?:[PF]\d|P0|P1|P2|✅|⚠️|❌))");

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            var severity = section.Contains("P0") || section.Contains("❌") ? "P0" :
                           section.Contains("P1") || section.Contains("⚠️") ? "P1" :
                           section.Contains("P2") || section.Contains("✅") ? "P2" : "P1";

            var refs = ExtractReferences(section);
            foreach (var (file, line, _) in refs)
            {
                // Extract the relevant body text
                var body = ExtractBodyAroundRef(section, file, line);
                comments.Add(new ReviewComment(
                    FilePath: file,
                    LineNumber: line,
                    LineEnd: line,
                    Severity: severity,
                    Category: InferCategory(section),
                    Title: body.Length > 80 ? body[..80] + "..." : body,
                    Body: body));
            }

            // If no file refs found but section has content, create a general comment
            if (refs.Count == 0)
            {
                var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                   .Where(l => !l.StartsWith("###") && !l.StartsWith("---"))
                                   .ToList();
                if (lines.Count > 0)
                {
                    comments.Add(new ReviewComment(
                        FilePath: "",
                        LineNumber: 0,
                        LineEnd: 0,
                        Severity: severity,
                        Category: InferCategory(section),
                        Title: lines[0].TrimStart('-', ' ', '*').Length > 80
                            ? lines[0].TrimStart('-', ' ', '*')[..80] + "..."
                            : lines[0].TrimStart('-', ' ', '*'),
                        Body: string.Join("\n", lines)));
                }
            }
        }

        return comments;
    }

    private static string? FindClosestFile(string filePath, List<DiffFileInfo> diffFiles)
    {
        var fileName = Path.GetFileName(filePath);
        var candidates = diffFiles
            .Select(f => new
            {
                Path = f.FilePath,
                Name = Path.GetFileName(f.FilePath),
                Score = string.Equals(Path.GetFileName(f.FilePath), fileName, StringComparison.OrdinalIgnoreCase) ? 2
                      : Path.GetFileName(f.FilePath).Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains(Path.GetFileName(f.FilePath), StringComparison.OrdinalIgnoreCase) ? 1 : 0
            })
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ToList();

        return candidates.FirstOrDefault()?.Path;
    }

    private static string ExtractBodyAroundRef(string text, string file, int line)
    {
        var idx = text.IndexOf(file, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.Length > 200 ? text[..200] + "..." : text;

        var start = Math.Max(0, idx - 50);
        var end = Math.Min(text.Length, idx + 200);
        var snippet = text[start..end].Trim();
        return snippet;
    }

    private static string InferCategory(string section)
    {
        var lower = section.ToLowerInvariant();
        if (lower.Contains("security") || lower.Contains("注入") || lower.Contains("xss") || lower.Contains("sq"))
            return "security";
        if (lower.Contains("perform") || lower.Contains("性能") || lower.Contains("linq"))
            return "performance";
        if (lower.Contains("style") || lower.Contains("naming") || lower.Contains("format"))
            return "style";
        if (lower.Contains("test") || lower.Contains("coverage") || lower.Contains("测试"))
            return "test-coverage";
        return "correctness";
    }
}
