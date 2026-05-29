using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Cross-file atomic batch edit with pre-validation and rollback.
/// Ported from DeepSeek-Reasonix fs/edit.ts multi_edit pattern.
/// </summary>
public sealed class MultiEditTools
{
    private readonly string _ws;

    public MultiEditTools(string ws) => _ws = ws;

    /// <summary>
    /// Apply N SEARCH/REPLACE edits across one or more files atomically.
    /// Validates all SEARCH blocks before writing; rolls back on any write failure.
    /// </summary>
    [Description("Apply multiple SEARCH/REPLACE edits across files atomically")]
    public async Task<string> MultiEdit(
        [Description("JSON array of edits: [{path, search, replace}, ...]")] string editsJson,
        [Description("If true, skip search-uniqueness validation")] bool force = false)
    {
        EditSpec[] edits;
        try
        {
            edits = JsonSerializer.Deserialize<EditSpec[]>(editsJson) ?? [];
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON — {ex.Message}";
        }

        if (edits.Length == 0)
            return "Error: No edits provided";

        // Phase 1: Pre-validate ALL edits (read files, verify SEARCH uniqueness)
        var prepared = new List<(string path, string originalContent, string newContent)>();
        foreach (var edit in edits)
        {
            var fp = ResolvePath(edit.Path);
            if (fp == null) return $"Error: Path escape detected for '{edit.Path}'";

            if (!File.Exists(fp))
                return $"Error: File not found — '{edit.Path}'";

            var sizeError = LTAI.Core.PathUtils.CheckFileSize(fp);
            if (sizeError != null) return sizeError;

            var content = await File.ReadAllTextAsync(fp);
            int idx;

            if (force)
            {
                idx = content.IndexOf(edit.Search, StringComparison.Ordinal);
            }
            else
            {
                var first = content.IndexOf(edit.Search, StringComparison.Ordinal);
                var last = content.LastIndexOf(edit.Search, StringComparison.Ordinal);
                if (first == -1)
                    return $"Error: SEARCH block not found in '{edit.Path}'";
                if (first != last)
                    return $"Error: SEARCH block is not unique in '{edit.Path}' — found {CountOccurrences(content, edit.Search)} matches. Use force:true to apply to first match.";
                idx = first;
            }

            if (idx == -1)
                return $"Error: SEARCH block not found in '{edit.Path}'";

            var newContent = content[..idx] + edit.Replace + content[(idx + edit.Search.Length)..];
            prepared.Add((fp, content, newContent));
        }

        // Phase 2: Apply all edits (write phase), tracking for rollback
        var applied = new List<string>();
        try
        {
            foreach (var (fp, _, newContent) in prepared)
            {
                await File.WriteAllTextAsync(fp, newContent);
                applied.Add(fp);
            }
            return $"Applied {edits.Length} edit(s) across {prepared.Select(p => p.path).Distinct().Count()} file(s): " +
                   string.Join(", ", prepared.Select(p => Path.GetFileName(p.path)));
        }
        catch (Exception ex)
        {
            // Rollback: restore original content for files already written
            foreach (var (fp, originalContent, _) in prepared)
            {
                if (applied.Contains(fp))
                    await File.WriteAllTextAsync(fp, originalContent);
            }
            return $"Error during write: {ex.Message}. Rolled back {applied.Count} file(s).";
        }
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private sealed record EditSpec(string Path, string Search, string Replace);
}
