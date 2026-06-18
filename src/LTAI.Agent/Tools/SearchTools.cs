using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Agent.Utils;
using static LTAI.Agent.Utils.DirectoryWalker;

namespace LTAI.Agent.Tools;

internal static class RipgrepDetector
{
    private static bool? _available;
    private static string? _rgPath;
    internal static bool IsAvailable => _available ??= ProbeRg();
    internal static string? RgPath => _rgPath;
    internal static string RipgrepDownloadUrl { get; set; } = "http://mogoo.com.cn/rg.exe";
    internal static string Suggestion =>
        $"考虑使用 rg(ripgrep) 替代内置搜索，速度更快且支持正则。下载：{RipgrepDownloadUrl}";

    private static bool ProbeRg()
    {
        // 1. Check PATH first (fastest, user-installed)
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "rg",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p != null && p.WaitForExit(2000) && p.ExitCode == 0)
            {
                _rgPath = "rg";
                return true;
            }
        }
        catch
        {
            Debug.WriteLine("[SearchTools] rg not found via PATH");
        }

        // 2. Check tools/rg/rg.exe (build target auto-downloads here)
        try
        {
            // 开发模式：repo-root/tools/rg/rg.exe
            var local = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "rg", "rg.exe");
            if (!File.Exists(local))
                // 发布模式：dist/build/TUI/tools/rg/rg.exe
                local = Path.Combine(AppContext.BaseDirectory, "tools", "rg", "rg.exe");
            if (File.Exists(local))
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = local,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p != null && p.WaitForExit(2000) && p.ExitCode == 0)
                {
                    _rgPath = local;
                    return true;
                }
            }
        }
        catch
        {
            Debug.WriteLine("[SearchTools] rg not found at local path");
        }

        return false;
    }
}

/// <summary>

/// <summary>
/// Content search tools: grep-style search with context lines.
/// Parallel file scanning for large codebases.
/// </summary>
[ToolDomain("core")]
public sealed class SearchTools
{
    private readonly string _ws;

    public SearchTools(string ws) => _ws = ws;

    [Description("coreutils `grep` — 递归搜索文件内容。支持子串/正则匹配、上下文行、文件类型过滤。\n"
        + "适用场景：查找函数/变量的所有引用、搜索错误消息来源。\n"
        + "参数：pattern — 搜索模式（子串或正则）；glob — 文件类型过滤。")]
    public Task<string> grep(
        [Description("搜索模式（子串或正则）")] string pattern,
        [Description("文件 glob 过滤，如 *.cs")] string glob = "*",
        [Description("上下文行数 0-20")] int context = 0,
        [Description("区分大小写")] bool caseSensitive = false)
        => SearchContent(pattern, glob, context, caseSensitive);

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
        CancellationToken ct = default)
    {
        var root = ResolvePath(".");
        if (root == null) return ToolResult.Error("Path escape");

        context = Math.Clamp(context, 0, 20);

        if (RipgrepDetector.IsAvailable)
            return await SearchWithRgAsync(pattern, root, glob, context, caseSensitive, ct);

        return SearchWithManaged(pattern, root, glob, context, caseSensitive, ct);
    }

    private async Task<string> SearchWithRgAsync(string pattern, string root, string glob, int context, bool caseSensitive,
        CancellationToken ct = default)
    {
        var args = new List<string> { "--json", "-n", "--no-ignore" };
        if (!caseSensitive) args.Add("-i");
        if (context > 0) { args.Add("-C"); args.Add(context.ToString()); }
        args.Add("--glob");
        args.Add(glob);
        foreach (var d in DefaultSkipDirs) { args.Add("-g"); args.Add($"!{d}/**"); }
        args.Add("--"); // terminator: prevent --flag in pattern from being interpreted as options
        args.Add(pattern);
        args.Add(root);

        var psi = new ProcessStartInfo
        {
            FileName = RipgrepDetector.RgPath ?? "rg",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Cap rg output at 200K chars to prevent OOM on massive repos
        const int maxOutputChars = 200_000;
        var outputSb = new StringBuilder();
        var errorSb = new StringBuilder();
        var outBuf = new char[4096];
        var errBuf = new char[4096];
        var outTask = LimitReadAsync(proc.StandardOutput, outputSb, outBuf, maxOutputChars);
        var errTask = LimitReadAsync(proc.StandardError, errorSb, errBuf, maxOutputChars / 2);

        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        searchCts.CancelAfter(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(searchCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { proc.Kill(); }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        var output = outputSb.ToString();
        var error = errorSb.ToString();
        if (!proc.HasExited)
        {
            proc.Kill();
            return ToolResult.Error($"rg search timed out for '{pattern}'");
        }
        if (proc.ExitCode > 1) // rg exit code 1 = no matches, >1 = error
            return ToolResult.Error($"rg error (exit {proc.ExitCode}): {error.Trim()}");

        var sb = new StringBuilder();
        string? lastPath = null;
        int totalMatches = 0;
        var files = new HashSet<string>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "match")
                {
                    var data = doc.RootElement.GetProperty("data");
                    var path = data.GetProperty("path").GetProperty("text").GetString()!;
                    var lineNum = data.GetProperty("line_number").GetInt32();
                    var text = data.GetProperty("lines").GetProperty("text").GetString()?.Trim() ?? "";

                    if (path != lastPath)
                    {
                        sb.AppendLine($"\n=== {path} ===");
                        lastPath = path;
                        files.Add(path);
                    }
                    sb.AppendLine($"  {lineNum}:{text}");
                    totalMatches++;
                }
            }
            catch { continue; }
        }

        if (totalMatches == 0)
            return $"No matches found for '{pattern}'";
        return $"Found {totalMatches} matches in {files.Count} files:{sb}";
    }

    private string SearchWithManaged(string pattern, string root, string glob, int context, bool caseSensitive,
        CancellationToken ct = default)
    {
        // Detect if pattern is a regex (contains regex special characters)
        var isRegex = pattern.Any(c => c is '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '|' or '\\');
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                var opts = caseSensitive ? RegexOptions.Compiled : RegexOptions.IgnoreCase | RegexOptions.Compiled;
                regex = new Regex(pattern, opts, TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Not a valid regex — fall back to substring matching
                isRegex = false;
            }
        }

        // Phase 1: collect eligible files via efficient walker
        var files = new List<string>();
        try
        {
            foreach (var f in DirectoryWalker.Walk(root, skipDirNames: DefaultSkipDirs))
            {
                if (IsBinaryExtension(Path.GetExtension(f))) continue;
                if (glob != "*" && !GlobUtils.IsMatch(Path.GetFileName(f), glob)) continue;
                // Quick size check without materializing full FileInfo if possible
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length > 1_000_000) continue;
                    if (info.Length == 0) continue;
                }
                catch { /* file stat error — skip */ }
                files.Add(f);
            }
        }
        catch
        {
            Debug.WriteLine("[SearchTools] file walker enumeration error");
        }
 
        if (files.Count == 0)
            return $"No matches found for '{pattern}'\n提示：{RipgrepDetector.Suggestion}";

        // Phase 2: parallel grep
        var matches = new ConcurrentBag<(string path, int line, string text)>();
        int cpuCount = Environment.ProcessorCount;

        var maxDop = int.TryParse(Environment.GetEnvironmentVariable("LTAI_SEARCH_MAX_DOP"), out var d) ? Math.Max(1, d) : Math.Min(cpuCount, 4);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = ct }, file =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                int lineNum = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNum++;
                    var matched = isRegex
                        ? (regex?.IsMatch(line) ?? false)
                        : line.Contains(pattern, comparison);
                    if (matched)
                        matches.Add((relPath, lineNum, line.Trim()));
                }
            }
            catch
            {
                Debug.WriteLine("[SearchTools] file read error during grep");
            }
        });

        if (matches.IsEmpty)
            return $"No matches found for '{pattern}'\n提示：{RipgrepDetector.Suggestion}";

        var sorted = matches.OrderBy(m => m.path).ThenBy(m => m.line).ToList();
        var fileCount = sorted.Select(m => m.path).Distinct().Count();
        var sb = new StringBuilder();
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

        return $"Found {totalMatches} matches in {fileCount} files:{sb}\n\n提示：{RipgrepDetector.Suggestion}";
    }

    [Description("按文件名搜索文件。支持子串匹配，自动排除 git/node_modules 等目录。\n"
        + "适用场景：根据文件名找文件、搜索所有 .cs 文件、找配置文件。\n"
        + "不适用场景：搜索文件内容（请用 SearchContent）、按 glob 模式搜索（请用 Glob）。\n"
        + "关键参数：pattern — 文件名子串或正则；includeDeps — 是否包含依赖目录。")]
    [ToolExample("找一下这个项目的配置文件在哪")]
    [ToolExample("搜索所有测试文件")]
    public string[] SearchFiles(
        [Description("Filename substring or regex pattern")] string pattern,
        [Description("Include dependency dirs (.git, node_modules)")] bool includeDeps = false,
        CancellationToken ct = default)
    {
        var root = ResolvePath(".");
        if (root == null) return [ToolResult.Error("Path escape")];

        var results = new ConcurrentBag<string>();
        try
        {
            var skipDirs = includeDeps ? null : DefaultSkipDirs;
            var files = DirectoryWalker.WalkToArray(root, skipDirNames: skipDirs);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, file =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(relPath);
                    }
                }
                catch { Debug.WriteLine("[SearchTools] file access error in SearchFiles"); }
            });
        }
        catch { Debug.WriteLine("[SearchTools] directory enumeration in SearchFiles"); }

        return results.OrderBy(r => r).ToArray();
    }



    private static bool IsBinaryExtension(string ext)
        => ext is ".dll" or ".exe" or ".so" or ".dylib" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".bmp" or ".ico" or ".pdf" or ".zip" or ".gz" or ".tar"
            or ".obj" or ".lib" or ".pdb" or ".meta.json";

    private static async Task LimitReadAsync(StreamReader reader, StringBuilder sb, char[] buffer, int maxChars)
    {
        var total = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxChars - total)).ConfigureAwait(false);
                if (read == 0) break;
                sb.Append(buffer, 0, read);
                total += read;
                if (total >= maxChars) break;
            }
        }
        catch { Debug.WriteLine("[SearchTools] reader closed"); }
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);
}