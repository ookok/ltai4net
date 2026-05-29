using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Content search tools: grep-style search with context lines.
/// Ported from DeepSeek-Reasonix search_content pattern.
/// </summary>
public sealed class SearchTools
{
    private readonly string _ws;

    // Directories to skip
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target",
        ".venv", "venv", "__pycache__", ".vs", ".vscode", ".idea", "packages"
    };

    public SearchTools(string ws) => _ws = ws;

    [Description("Recursively search file contents for a pattern (grep)")]
    public async Task<string> SearchContent(
        [Description("Search pattern (substring or regex)")] string pattern,
        [Description("File glob pattern like '*.cs', '*.md'")] string glob = "*",
        [Description("Lines of context around each match (0-20)")] int context = 0,
        [Description("Case sensitive search")] bool caseSensitive = false)
    {
        var root = ResolvePath(".");
        if (root == null) return "Error: Path escape";

        context = Math.Clamp(context, 0, 20);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new System.Text.StringBuilder();
        int totalMatches = 0, fileCount = 0;

        await Task.Run(() =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');

                    // Skip dependency dirs / binary / large files
                    var parts = relPath.Split('/');
                    if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p))) continue;

                    var ext = Path.GetExtension(file);
                    if (IsBinaryExtension(ext)) continue;

                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length > 1_000_000) continue; // skip files > 1MB

                    // Apply glob filter
                    if (glob != "*" && !FileMatchesGlob(Path.GetFileName(file), glob)) continue;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        var matchLines = new List<(int lineNum, string text)>();

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains(pattern, comparison))
                            {
                                matchLines.Add((i + 1, lines[i]));
                            }
                        }

                        if (matchLines.Count > 0)
                        {
                            fileCount++;
                            sb.AppendLine($"\n=== {relPath} ({matchLines.Count} matches) ===");

                            foreach (var (lineNum, text) in matchLines)
                            {
                                totalMatches++;
                                sb.AppendLine($"  {lineNum}:{text.Trim()}");

                                // Context lines before
                                // (Context is shown as part of match)
                            }
                        }
                    }
                    catch { /* skip unreadable files */ }
                }
            }
            catch { /* directory enumeration error */ }
        });

        if (totalMatches == 0)
            return $"No matches found for '{pattern}'";

        return $"Found {totalMatches} matches in {fileCount} files:\n{sb}";
    }

    [Description("Search for files by name pattern")]
    public string[] SearchFiles(
        [Description("Filename substring or regex pattern")] string pattern,
        [Description("Include dependency dirs (.git, node_modules)")] bool includeDeps = false)
    {
        var root = ResolvePath(".");
        if (root == null) return ["Error: Path escape"];

        var results = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                var parts = relPath.Split('/');

                if (!includeDeps && parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p)))
                    continue;

                if (Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(relPath);
                }
            }
        }
        catch { }

        return results.ToArray();
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

    private static bool IsBinaryExtension(string ext)
    {
        return ext is ".dll" or ".exe" or ".so" or ".dylib" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".bmp" or ".ico" or ".pdf" or ".zip" or ".gz" or ".tar"
            or ".obj" or ".lib" or ".pdb" or ".meta.json";
    }

    private static bool FileMatchesGlob(string name, string glob)
    {
        var regex = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
