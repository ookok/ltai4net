using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Glob pattern file matching with sort-by-mtime.
/// Ported from DeepSeek-Reasonix fs/glob.ts pattern.
/// </summary>
public sealed class GlobTools
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target",
        ".venv", "venv", "__pycache__", ".vs", ".vscode", ".idea"
    };

    private readonly string _ws;

    public GlobTools(string ws) => _ws = ws;

    [Description("Find files matching a glob pattern, sorted by last-modified time")]
    public string[] Glob(
        [Description("Glob pattern like 'src/**/*.cs', '**/*.{md,mdx}'")] string pattern,
        [Description("Base directory (default: workspace root)")] string path = ".",
        [Description("Sort by: 'mtime' (default) or 'name'")] string sortBy = "mtime",
        [Description("Max results (1-1000, default: 200)")] int limit = 200,
        [Description("If true, include .git/node_modules etc.")] bool includeDeps = false)
    {
        var root = ResolvePath(path);
        if (root == null) return ["Error: Path escape"];
        if (!Directory.Exists(root)) return ["Error: Directory not found"];

        limit = Math.Clamp(limit, 1, 1000);

        // Convert glob-like pattern to simple recursive search
        // Split pattern into directory part and file pattern
        var dirPart = Path.GetDirectoryName(pattern)?.Replace('\\', '/') ?? ".";
        var filePattern = Path.GetFileName(pattern);

        // Handle ** in path
        var searchDir = dirPart.Contains("**")
            ? root
            : Path.GetFullPath(Path.Combine(root, dirPart));

        // Resolve ** to SearchOption
        var searchOption = dirPart.Contains("**") || pattern.StartsWith("**")
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        // Convert glob to regex
        var regex = GlobToRegex(filePattern);

        var results = new List<(string path, DateTime mtime)>();

        try
        {
            var searchRoot = dirPart.Contains("**")
                ? root
                : (Directory.Exists(Path.Combine(root, dirPart))
                    ? Path.GetFullPath(Path.Combine(root, dirPart))
                    : root);

            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", searchOption))
            {
                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');

                // Skip dependency dirs
                if (!includeDeps)
                {
                    var parts = relPath.Split('/');
                    if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p)))
                        continue;
                }

                if (regex.IsMatch(Path.GetFileName(file)))
                {
                    try
                    {
                        var mtime = File.GetLastWriteTimeUtc(file);
                        results.Add((relPath, mtime));
                    }
                    catch
                    {
                        results.Add((relPath, DateTime.MinValue));
                    }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return [$"Error: Directory not found: {searchDir}"];
        }

        // Sort
        if (sortBy == "name")
            results.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
        else
            results.Sort((a, b) => b.mtime.CompareTo(a.mtime)); // newest first

        return results.Take(limit).Select(r => r.path).ToArray();
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".")
            .Replace(@"{", "(?:")
            .Replace(@",", "|")
            .Replace(@"}", ")");

        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
