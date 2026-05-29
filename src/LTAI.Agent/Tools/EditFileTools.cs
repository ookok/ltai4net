using System.ComponentModel;

namespace LTAI.Agent.Tools;

/// <summary>
/// SEARCH/REPLACE file editing with uniqueness validation and read-before-edit enforcement.
/// Ported from DeepSeek-Reasonix fs/edit.ts pattern.
/// </summary>
public sealed class EditFileTools
{
    private readonly string _ws;

    // Tracks files read this session for edit-before-read prevention
    private readonly HashSet<string> _readTracker = new(StringComparer.OrdinalIgnoreCase);

    public EditFileTools(string ws) => _ws = ws;

    /// <summary>
    /// Mark a path as read (called automatically when tools read files).
    /// </summary>
    public void MarkRead(string path)
    {
        var fp = ResolvePath(path);
        if (fp != null) _readTracker.Add(fp);
    }

    [Description("Apply a SEARCH/REPLACE edit to an existing file. Call read_file first — SEARCH text must match exactly.")]
    public async Task<string> EditFile(
        [Description("File path relative to workspace")] string path,
        [Description("Exact text to find (must be unique in the file)")] string search,
        [Description("Replacement text")] string replace)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";

        if (!_readTracker.Contains(fp))
            return "Error: File has not been read this session — call read_file first. " +
                   "This prevents editing files without knowing their current content.";

        if (!File.Exists(fp))
            return $"Error: File not found — '{path}'";

        var sizeError = LTAI.Core.PathUtils.CheckFileSize(fp);
        if (sizeError != null) return sizeError;

        var content = await File.ReadAllTextAsync(fp);

        var first = content.IndexOf(search, StringComparison.Ordinal);
        var last = content.LastIndexOf(search, StringComparison.Ordinal);

        if (first == -1)
            return $"Error: SEARCH block not found in '{path}'";

        if (first != last)
            return $"Error: SEARCH block is not unique in '{path}' — found {CountOccurrences(content, search)} matches";

        var newContent = content[..first] + replace + content[(first + search.Length)..];
        await File.WriteAllTextAsync(fp, newContent);
        return $"Applied edit to '{path}' ({search.Length} chars replaced → {replace.Length} chars)";
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
}
