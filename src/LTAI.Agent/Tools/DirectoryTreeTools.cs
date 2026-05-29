using System.ComponentModel;
using System.Text;

namespace LTAI.Agent.Tools;

/// <summary>
/// Recursive directory tree listing with smart auto-collapse.
/// Ported from DeepSeek-Reasonix filesystem.ts directory_tree pattern.
/// Skips dependency/VCS/build directories by default; collapses large subtrees.
/// </summary>
public sealed class DirectoryTreeTools
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", ".venv", "venv", "__pycache__",
        "bin", "obj", "dist", "build", "target", ".next", ".nuxt", ".turbo",
        ".vercel", ".cache", ".direnv", ".devenv", ".mypy_cache", ".pytest_cache",
        "packages", ".vs", ".vscode", ".idea"
    };

    private const int MaxChildren = 50;
    private const int DefaultMaxDepth = 2;

    private readonly string _ws;

    public DirectoryTreeTools(string ws) => _ws = ws;

    [Description("Recursively list directory structure with auto-collapse")]
    public async Task<string> DirectoryTree(
        [Description("Root path (default: workspace)")] string path = ".",
        [Description("Maximum recursion depth (1-5, default: 2)")] int maxDepth = DefaultMaxDepth,
        [Description("If true, include dependency directories (.git, node_modules, etc.)")] bool includeDeps = false)
    {
        var root = ResolvePath(path);
        if (root == null) return "Error: Path escape";
        if (!Directory.Exists(root)) return "Error: Directory not found";

        maxDepth = Math.Clamp(maxDepth, 1, 5);
        var sb = new StringBuilder();
        await BuildTreeAsync(root, "", 0, maxDepth, includeDeps, sb);
        return sb.ToString();
    }

    private async Task BuildTreeAsync(string dir, string prefix, int depth, int maxDepth, bool includeDeps, StringBuilder sb)
    {
        if (depth > maxDepth) return;

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{prefix}[access denied]");
            return;
        }

        var visible = includeDeps
            ? entries.ToList()
            : entries.Where(e =>
            {
                var name = Path.GetFileName(e);
                return !SkipDirs.Contains(name);
            }).ToList();

        if (visible.Count > MaxChildren)
        {
            sb.AppendLine($"{prefix}[{visible.Count} entries — use list_directory to inspect]");
            // Still list directories at this level for navigation
            var subdirs = visible.Where(Directory.Exists).ToList();
            foreach (var entry in subdirs.Take(10))
            {
                var name = Path.GetFileName(entry);
                sb.AppendLine($"{prefix}  {name}/");
                // Don't recurse into collapsed
            }
            if (subdirs.Count > 10)
                sb.AppendLine($"{prefix}  … and {subdirs.Count - 10} more subdirectories");
            return;
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var entry = visible[i];
            var name = Path.GetFileName(entry);
            var isLast = i == visible.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var childPrefix = isLast ? "    " : "│   ";

            if (Directory.Exists(entry))
            {
                sb.AppendLine($"{prefix}{connector}{name}/");
                await BuildTreeAsync(entry, prefix + childPrefix, depth + 1, maxDepth, includeDeps, sb);
            }
            else
            {
                sb.AppendLine($"{prefix}{connector}{name}");
            }
        }
    }

    private string? ResolvePath(string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        return fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase) ? fp : null;
    }
}
