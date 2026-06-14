using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;

namespace LTAI.Agent.Tools;

[ToolDomain("code")]
public sealed class TextTools
{
    private readonly string _ws;
    public TextTools(string ws) => _ws = ws;

    // ========== EDIT FILE ==========

    [Description("在文件中执行 SEARCH/REPLACE 精确文本替换。SEARCH 必须唯一匹配，否则拒绝执行。\n"
        + "适用场景：修改单个文件中的代码片段、重命名局部变量、修改配置值、修复代码 bug。\n"
        + "不适用场景：跨文件批量编辑（请用 MultiEdit）、创建新文件（请用 WriteFile）、正则替换（请用 RegexTest 确认模式后再手动编辑）。\n"
        + "关键参数：path — 文件路径；search — 要搜索的精确文本；replace — 替换后的文本。")]
    public async Task<string> EditFile(string path, string search, string replace)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        if (!File.Exists(fp)) return "File not found";
        var sizeError = PathUtils.CheckFileSize(fp);
        if (sizeError != null) return sizeError;

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var first = content.IndexOf(search, StringComparison.Ordinal);
        var last = content.LastIndexOf(search, StringComparison.Ordinal);

        if (first == -1) return "Error: SEARCH text not found in file";
        if (first != last) return $"Error: SEARCH text appears {CountOccurrences(content, search)} times. Use MultiEdit with force:true for ambiguous matches.";

        var newContent = content[..first] + replace + content[(first + search.Length)..];
        await AtomicWriteAsync(fp, newContent).ConfigureAwait(false);
        return $"Edited {Path.GetFileName(fp)}: replaced \"{search[..Math.Min(search.Length, 50)]}\"";
    }

    // ========== MULTI EDIT ==========

    [Description("在多个文件中批量应用 SEARCH/REPLACE 编辑，原子提交（验证全部通过后才写入）。失败时自动回滚。\n"
        + "适用场景：跨文件重命名变量/函数、批量修改多个文件中的相同模式、大规模代码重构。\n"
        + "不适用场景：只改一个文件（请用 EditFile）、创建新文件（请用 WriteFile）。\n"
        + "关键参数：editsJson — SEARCH/REPLACE 编辑数组 JSON：[{path, search, replace}, ...]；force — 跳过唯一性校验。")]
    public async Task<string> MultiEdit(string editsJson, bool force = false)
    {
        EditSpec[] edits;
        try { edits = JsonSerializer.Deserialize<EditSpec[]>(editsJson) ?? []; }
        catch (JsonException ex) { return $"Error: Invalid JSON -- {ex.Message}"; }
        if (edits.Length == 0) return "Error: No edits provided";

        var prepared = new List<(string path, string original, string updated)>();
        foreach (var edit in edits)
        {
            var fp = SafePath(edit.Path);
            if (fp == null) return $"Error: Path escape -- '{edit.Path}'";
            if (!File.Exists(fp)) return $"Error: File not found -- '{edit.Path}'";
            var sizeError = PathUtils.CheckFileSize(fp);
            if (sizeError != null) return sizeError;

            var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
            int idx = force ? content.IndexOf(edit.Search, StringComparison.Ordinal) :
                content.IndexOf(edit.Search, StringComparison.Ordinal);
            if (idx == -1) return $"Error: SEARCH not found in '{edit.Path}'";
            if (!force)
            {
                var last = content.LastIndexOf(edit.Search, StringComparison.Ordinal);
                if (idx != last)  // Use the local variable `first` — already computed above
                {
                    // Recompute first outside the else block logic
                    var f = content.IndexOf(edit.Search, StringComparison.Ordinal);
                    var l = content.LastIndexOf(edit.Search, StringComparison.Ordinal);
                    if (f != l) return $"Error: SEARCH not unique in '{edit.Path}'. Use force:true for first-match.";
                }
            }
            var updated = content[..idx] + edit.Replace + content[(idx + edit.Search.Length)..];
            prepared.Add((fp, content, updated));
        }

        var applied = new List<string>();
        try
        {
            foreach (var (fp, _, updated) in prepared)
            {
                await AtomicWriteAsync(fp, updated).ConfigureAwait(false);
                applied.Add(fp);
            }
            return $"Applied {edits.Length} edit(s) across {prepared.Select(p => p.path).Distinct().Count()} file(s): "
                + string.Join(", ", prepared.Select(p => Path.GetFileName(p.path)));
        }
        catch (Exception ex)
        {
            // Rollback: atomic write with original content
            foreach (var (fp, original, _) in prepared)
                if (applied.Contains(fp)) await AtomicWriteAsync(fp, original).ConfigureAwait(false);
            return $"Error during write: {ex.Message}. Rolled back {applied.Count} file(s).";
        }
    }

    // ========== REGEX TEST ==========

    [Description("测试正则表达式匹配。返回匹配结果列表，包含每个匹配的位置和分组。\n"
        + "适用场景：调试正则表达式、验证字符串模式、提取匹配内容、确认正则后再用于 EditFile/MultiEdit。\n"
        + "关键参数：pattern — 正则表达式；input — 要匹配的字符串；options — 可选如 IgnoreCase,Multiline,Singleline。")]
    public static string RegexTest(string pattern, string input, string? options = null)
    {
        try
        {
            var opts = RegexOptions.None;
            if (options != null)
            {
                foreach (var opt in options.Split(','))
                {
                    opts |= opt.Trim() switch
                    {
                        "IgnoreCase" or "ignorecase" => RegexOptions.IgnoreCase,
                        "Multiline" or "multiline" => RegexOptions.Multiline,
                        "Singleline" or "singleline" => RegexOptions.Singleline,
                        "Compiled" => RegexOptions.Compiled,
                        "ExplicitCapture" => RegexOptions.ExplicitCapture,
                        _ => RegexOptions.None
                    };
                }
            }

            var regex = new Regex(pattern, opts);
            var matches = regex.Matches(input);
            if (matches.Count == 0) return "No matches found.";

            var results = new List<string>();
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var groups = string.Join("\n", m.Groups.Cast<Group>().Select((g, gi) =>
                    $"  Group[{gi}]: '{g.Value}' (idx={g.Index}, len={g.Length})"));
                results.Add($"Match[{i}]: '{m.Value}' (idx={m.Index})\n{groups}");
            }
            return $"Pattern: {pattern}\nMatches: {matches.Count}\n\n{string.Join("\n\n", results)}";
        }
        catch (Exception ex)
        {
            return $"Regex error: {ex.Message}";
        }
    }

    // ========== DIFF ==========

    [Description("比较两个文件或两段文本的内容差异，返回 unified diff 格式。\n"
        + "适用场景：代码差异对比、配置文件比对、版本变更分析。\n"
        + "不适用场景：Git 提交间的比较（请用 GitDiff）。\n"
        + "关键参数：leftPath — 左文件路径或文本；rightPath — 右文件路径或文本。")]
    public static string DiffFiles(string leftPath, string rightPath)
    {
        try
        {
            string leftText, rightText, leftName, rightName;
            if (File.Exists(leftPath) && File.Exists(rightPath))
            {
                leftText = File.ReadAllText(leftPath);
                rightText = File.ReadAllText(rightPath);
                leftName = Path.GetFileName(leftPath);
                rightName = Path.GetFileName(rightPath);
            }
            else
            {
                leftText = leftPath; rightText = rightPath;
                leftName = "left"; rightName = "right";
            }

            var lcs = ComputeLcs(leftText.Split('\n'), rightText.Split('\n'));
            var sb = new StringBuilder();
            sb.AppendLine($"--- {leftName}");
            sb.AppendLine($"+++ {rightName}");
            var diff = FormatDiff(leftText.Split('\n'), rightText.Split('\n'), lcs);
            foreach (var hunk in diff) sb.AppendLine(hunk);

            var result = sb.ToString();
            return result.Length > 20000 ? ContentTruncator.Truncate(result, 20000) : result;
        }
        catch (Exception ex) { return $"Diff error: {ex.Message}"; }
    }

    // ========== PRIVATE ==========

    private string? SafePath(string path) => PathUtils.SafeResolvePath(_ws, path);
    private static int CountOccurrences(string text, string pattern)
    {
        int c = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1) { c++; i += pattern.Length; }
        return c;
    }

    /// <summary>Atomic file write: write to .tmp.{guid} then File.Move (atomic on NTFS).</summary>
    private static async Task AtomicWriteAsync(string path, string content)
    {
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private sealed record EditSpec(string Path, string Search, string Replace);

    private static List<string> ComputeLcs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        // 使用 2 行滚动数组，避免 O(m*n) 内存
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
                curr[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
        }

        // 回溯重建 LCS（用 prev 数组 + 原始序列）
        var result = new List<string>();
        int x = m, y = n;
        // 重建时需要原始 dp 值，重新计算仅保留最后两行不够
        // 改用 Hirschberg 算法或直接限制最大文件大小
        // 实际场景限制：差异行数超过 1000 时截断
        if (m > 1000 || n > 1000)
        {
            result.Add("...(diff too large, truncated)");
            return result;
        }
        // 小文件用完整矩阵
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1]) { result.Add(a[x - 1]); x--; y--; }
            else if (dp[x - 1, y] > dp[x, y - 1]) x--;
            else y--;
        }
        result.Reverse();
        return result;
    }

    private static List<string> FormatDiff(string[] left, string[] right, List<string> lcs)
    {
        var result = new List<string>();
        int li = 0, ri = 0, ci = 0;

        while (li < left.Length || ri < right.Length)
        {
            var diffStart = li;
            while (ci < lcs.Count && li < left.Length && ri < right.Length && left[li] == lcs[ci]) { li++; ri++; ci++; }

            var leftEnd = li;
            var rightEnd = ri;
            while (ci < lcs.Count && li < left.Length && left[li] != lcs[ci]) li++;
            while (ci < lcs.Count && ri < right.Length && right[ri] != lcs[ci]) ri++;

            if (li > leftEnd || ri > rightEnd)
            {
                var ctx = Math.Max(0, leftEnd - 3);
                var hunk = new StringBuilder();
                hunk.AppendLine($"@@ -{ctx + 1},{left.Length - ctx} +{ctx + 1},{right.Length - ctx} @@");
                for (int i = ctx; i < leftEnd; i++) hunk.AppendLine($" {left[i].TrimEnd('\r')}");
                for (int i = leftEnd; i < li; i++) hunk.AppendLine($"-{left[i].TrimEnd('\r')}");
                for (int i = rightEnd; i < ri; i++) hunk.AppendLine($"+{right[i].TrimEnd('\r')}");
                for (int i = Math.Max(li, ri); i < left.Length && i < right.Length; i++) hunk.AppendLine($" {left[i].TrimEnd('\r')}");
                result.Add(hunk.ToString().TrimEnd());
            }

            if (ci < lcs.Count) { li++; ri++; ci++; }
            else
            {
                if (li < left.Length || ri < right.Length)
                {
                    var hunk = new StringBuilder();
                    hunk.AppendLine($"@@ -{li + 1},{left.Length - li} +{ri + 1},{right.Length - ri} @@");
                    for (int i = li; i < left.Length; i++) hunk.AppendLine($"-{left[i].TrimEnd('\r')}");
                    for (int i = ri; i < right.Length; i++) hunk.AppendLine($"+{right[i].TrimEnd('\r')}");
                    result.Add(hunk.ToString().TrimEnd());
                }
                break;
            }
        }
        return result;
    }
}
