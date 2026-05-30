using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Content search tools: grep-style search with context lines.
/// Parallel file scanning for large codebases.
/// </summary>
public sealed class SearchTools
{
    private readonly string _ws;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target",
        ".venv", "venv", "__pycache__", ".vs", ".vscode", ".idea", "packages"
    };

    public SearchTools(string ws) => _ws = ws;

    [Description("Recursively search file contents for a pattern (grep). 路径越界需用户确认。")]
    public async Task<string> SearchContent(
        [Description("Search pattern (substring or regex)")] string pattern,
        [Description("File glob pattern like '*.cs', '*.md'")] string glob = "*",
        [Description("Lines of context around each match (0-20)")] int context = 0,
        [Description("Case sensitive search")] bool caseSensitive = false,
        [Description("跨沙箱确认标记")] bool confirm = false)
    {
        var root = ResolvePath(".");
        if (root == null) return "Error: Path escape";

        context = Math.Clamp(context, 0, 20);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Phase 1: collect eligible files (fast sequential walk)
        var files = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(root, f).Replace('\\', '/');
                var parts = relPath.Split('/');
                if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p))) continue;
                if (IsBinaryExtension(Path.GetExtension(f))) continue;
                if (new FileInfo(f).Length > 1_000_000) continue;
                if (glob != "*" && !FileMatchesGlob(Path.GetFileName(f), glob)) continue;
                files.Add(f);
            }
        }
        catch { /* directory enumeration error — no matching files */ }

        if (files.Count == 0)
            return $"No matches found for '{pattern}'";

        // Phase 2: parallel grep
        var matches = new ConcurrentBag<(string path, int line, string text)>();
        int cpuCount = Environment.ProcessorCount;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = cpuCount }, file =>
        {
            try
            {
                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                int lineNum = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNum++;
                    if (line.Contains(pattern, comparison))
                    {
                        matches.Add((relPath, lineNum, line.Trim()));
                    }
                }
            }
            catch { /* file read error — skip */ }
        });

        if (matches.IsEmpty)
            return $"No matches found for '{pattern}'";

        // Sort by path then line for stable output
        var sorted = matches.OrderBy(m => m.path).ThenBy(m => m.line).ToList();
        var fileCount = sorted.Select(m => m.path).Distinct().Count();
        var sb = new System.Text.StringBuilder();
        string? lastPath = null;
        int totalMatches = 0;

        foreach (var (path, line, text) in sorted)
        {
            if (path != lastPath)
            {
                sb.AppendLine($"\n=== {path} ===");
                lastPath = path;
            }
            sb.AppendLine($"  {line}:{text}");
            totalMatches++;
        }

        return $"Found {totalMatches} matches in {fileCount} files:\n{sb}";
    }

    [Description("Search for files by name pattern (parallel)")]
    public string[] SearchFiles(
        [Description("Filename substring or regex pattern")] string pattern,
        [Description("Include dependency dirs (.git, node_modules)")] bool includeDeps = false)
    {
        var root = ResolvePath(".");
        if (root == null) return ["Error: Path escape"];

        var results = new ConcurrentBag<string>();
        try
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                try
                {
                    var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var parts = relPath.Split('/');
                    if (!includeDeps && parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p)))
                        return;
                    if (Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(relPath);
                    }
                }
                catch { /* file access error — skip */ }
            });
        }
        catch { /* directory enumeration error */ }

        return results.OrderBy(r => r).ToArray();
    }



    private static bool IsBinaryExtension(string ext)
        => ext is ".dll" or ".exe" or ".so" or ".dylib" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".bmp" or ".ico" or ".pdf" or ".zip" or ".gz" or ".tar"
            or ".obj" or ".lib" or ".pdb" or ".meta.json";

    private static bool FileMatchesGlob(string name, string glob)
    {
        var regex = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);
}