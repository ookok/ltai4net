using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.RegularExpressions;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Content search tools: grep-style search with context lines.
/// Parallel file scanning for large codebases.
/// </summary>
[ToolDomain("core")]
public sealed class SearchTools
{
    private readonly string _ws;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target",
        ".venv", "venv", "__pycache__", ".vs", ".vscode", ".idea", "packages"
    };

    public SearchTools(string ws) => _ws = ws;

    [Description("递归搜索文件内容（grep 风格）。支持子串/正则匹配、上下文行显示、文件类型过滤。\n"
        + "适用场景：查找某个函数或变量的所有引用位置、搜索日志中的特定错误模式、统计代码中某个模式的出现次数。\n"
        + "不适用场景：查找文件名（请用 SearchFiles）、目录遍历浏览（请用 DirectoryTree）。\n"
        + "关键参数：pattern — 搜索模式（子串或正则）；glob — 文件类型过滤如 *.cs *.md；context — 上下文行数。")]
    [ToolExample("搜索所有用到 HttpClient 的地方")]
    [ToolExample("查找日志里的 ERROR 关键字")]
    [ToolExample("搜一下哪个文件定义了这个类")]
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
            var rootSpan = root.AsSpan();
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // 检查跳过目录（不分配 Split 数组）
                var relPath = Path.GetRelativePath(root, f).Replace('\\', '/');
                var relSpan = relPath.AsSpan();
                var skip = false;
                while (relSpan.Length > 0)
                {
                    var slashIdx = relSpan.IndexOf('/');
                    var seg = slashIdx >= 0 ? relSpan[..slashIdx] : relSpan;
                    if (seg.Length > 0 && SkipDirs.Contains(seg.ToString())) { skip = true; break; }
                    if (slashIdx < 0) break;
                    relSpan = relSpan[(slashIdx + 1)..];
                }
                if (skip) continue;

                if (IsBinaryExtension(Path.GetExtension(f))) continue;
                try
                {
                    if (new FileInfo(f).Length > 1_000_000) continue;
                }
                catch { continue; }
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

    [Description("按文件名搜索文件。支持子串匹配，自动排除 git/node_modules 等目录。\n"
        + "适用场景：根据文件名找文件、搜索所有 .cs 文件、找配置文件。\n"
        + "不适用场景：搜索文件内容（请用 SearchContent）、按 glob 模式搜索（请用 Glob）。\n"
        + "关键参数：pattern — 文件名子串或正则；includeDeps — 是否包含依赖目录。")]
    [ToolExample("找一下这个项目的配置文件在哪")]
    [ToolExample("搜索所有测试文件")]
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

    // Bounded glob→regex cache (LRU, max 256 entries)
    private static readonly ConcurrentDictionary<string, Regex> _globCache = new(4, 256, StringComparer.OrdinalIgnoreCase);
    private const int GlobCacheMax2 = 256;
    private static int _globCount2;

    private static bool FileMatchesGlob(string name, string glob)
    {
        if (!_globCache.TryGetValue(glob, out var regex))
        {
            if (Interlocked.Increment(ref _globCount2) > GlobCacheMax2) { _globCache.Clear(); Interlocked.Exchange(ref _globCount2, 0); }
            var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            _globCache.TryAdd(glob, regex);
            if (!_globCache.TryGetValue(glob, out var r)) return regex.IsMatch(name);
            regex = r;
        }
        return regex.IsMatch(name);
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);
}