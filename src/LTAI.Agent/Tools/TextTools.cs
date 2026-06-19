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

    [Description("���ļ���ִ�� SEARCH/REPLACE ��ȷ�ı��滻��SEARCH ����Ψһƥ�䣬����ܾ�ִ�С�\n"
        + "���ó������޸ĵ����ļ��еĴ���Ƭ�Ρ��������ֲ��������޸�����ֵ���޸����� bug��\n"
        + "�����ó��������ļ������༭������ MultiEdit�����������ļ������� WriteFile���������滻������ RegexTest ȷ��ģʽ�����ֶ��༭����\n"
        + "�ؼ�������path �� �ļ�·����search �� Ҫ�����ľ�ȷ�ı���replace �� �滻����ı���")]
    public async Task<string> EditFile(string path, string search, string replace)
    {
        var fp = SafePath(path);
        if (fp == null) return ToolResult.Error("path escape");
        if (!File.Exists(fp)) return ToolResult.Error("File not found");
        var sizeError = PathUtils.CheckFileSize(fp);
        if (sizeError != null) return ToolResult.Error(sizeError);

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var first = content.IndexOf(search, StringComparison.Ordinal);
        var last = content.LastIndexOf(search, StringComparison.Ordinal);

        if (first == -1) return ToolResult.Error("SEARCH text not found in file");
        if (first != last) return ToolResult.Error($"SEARCH text appears {CountOccurrences(content, search)} times. Use MultiEdit with force:true for ambiguous matches.");

        var newContent = content[..first] + replace + content[(first + search.Length)..];
        await AtomicWriteAsync(fp, newContent).ConfigureAwait(false);
        return $"Edited {Path.GetFileName(fp)}: replaced \"{search[..Math.Min(search.Length, 50)]}\"";
    }

    // ========== MULTI EDIT ==========

    [Description("�ڶ���ļ�������Ӧ�� SEARCH/REPLACE �༭��ԭ���ύ����֤ȫ��ͨ�����д�룩��ʧ��ʱ�Զ��ع���\n"
        + "���ó��������ļ�����������/�����������޸Ķ���ļ��е���ͬģʽ�����ģ�����ع���\n"
        + "�����ó�����ֻ��һ���ļ������� EditFile�����������ļ������� WriteFile����\n"
        + "�ؼ�������editsJson �� SEARCH/REPLACE �༭���� JSON��[{path, search, replace}, ...]��force �� ����Ψһ��У�顣")]
    public async Task<string> MultiEdit(string editsJson, bool force = false)
    {
        EditSpec[] edits;
        try { edits = JsonSerializer.Deserialize<EditSpec[]>(editsJson) ?? []; }
        catch (JsonException ex) { return ToolResult.Error($"Invalid JSON -- {ex.Message}"); }
        if (edits.Length == 0) return ToolResult.Error("No edits provided");

        var prepared = new List<(string path, string original, string updated)>();
        foreach (var edit in edits)
        {
            var fp = SafePath(edit.Path);
            if (fp == null) return ToolResult.Error($"Path escape -- '{edit.Path}'");
            if (!File.Exists(fp)) return ToolResult.Error($"File not found -- '{edit.Path}'");
            var sizeError = PathUtils.CheckFileSize(fp);
            if (sizeError != null) return ToolResult.Error(sizeError);

            var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
            int idx = content.IndexOf(edit.Search, StringComparison.Ordinal);
            if (idx == -1) return ToolResult.Error($"SEARCH not found in '{edit.Path}'");
            if (!force)
            {
                var last = content.LastIndexOf(edit.Search, StringComparison.Ordinal);
                if (idx != last)
                    return ToolResult.Error($"SEARCH not unique in '{edit.Path}'. Use force:true for first-match.");
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
            return ToolResult.Error($"{ex.Message}. Rolled back {applied.Count} file(s).");
        }
    }

    // ========== REGEX TEST ==========

    [Description("����������ʽƥ�䡣����ƥ�����б������ÿ��ƥ���λ�úͷ��顣\n"
        + "���ó���������������ʽ����֤�ַ���ģʽ����ȡƥ�����ݡ�ȷ������������� EditFile/MultiEdit��\n"
        + "�ؼ�������pattern �� ������ʽ��input �� Ҫƥ����ַ�����options �� ��ѡ�� IgnoreCase,Multiline,Singleline��")]
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
            return ToolResult.Error($"{ex.Message}");
        }
    }

    // ========== APPLY UNIFIED DIFF ==========

    [Description("�� unified diff ����Ӧ�õ��ļ���֧�ֱ�׼ diff -u ��ʽ��git diff �����\n"
        + "ÿ�� hunk ����֤ context lines ƥ����Ӧ�ã�ʧ��ʱ�Զ��ع���\n"
        + "���ó�����Ӧ�� code review ���顢ͬ�� git diff �������޸ġ�\n"
        + "�ؼ�������path �� �ļ�·����diff �� unified diff �ı����� @@ hunk headers����")]
    public async Task<string> ApplyUnifiedDiff(
        [Description("�ļ�·��")] string path,
        [Description("unified diff �ı�����ʽ�磺@@ -1,3 +1,4 @@\\n context\\n-old\\n+new")] string diff)
    {
        var fp = SafePath(path);
        if (fp == null) return ToolResult.Error("path escape");
        if (!File.Exists(fp)) return ToolResult.Error("File not found");

        var lines = await File.ReadAllLinesAsync(fp).ConfigureAwait(false);
        var hunks = ParseUnifiedHunks(diff);
        if (hunks.Count == 0) return ToolResult.Error("No valid hunks found in diff");

        var result = new List<string>(lines);
        var applied = 0;
        var failedHunks = new List<int>();

        for (int hi = 0; hi < hunks.Count; hi++)
        {
            var (oldStart, hunkLines) = hunks[hi];

            // Find the anchor: match first context or removed line in current result
            var anchorIdx = -1;
            for (int i = 0; i < result.Count; i++)
            {
                var firstHunkLine = hunkLines[0];
                var content = firstHunkLine.Length > 1 ? firstHunkLine[1..] : "";
                if (result[i] == content)
                {
                    anchorIdx = i;
                    break;
                }
            }

            if (anchorIdx < 0)
            {
                failedHunks.Add(hi);
                continue;
            }

            // Verify context lines and apply
            var canApply = true;
            var ri = anchorIdx;
            var toRemove = new List<int>();
            var toInsert = new List<(int index, string line)>();

            foreach (var hl in hunkLines)
            {
                if (hl.Length == 0) continue;
                // Skip "\ No newline at end of file" markers
                if (hl[0] == '\\')
                    continue;
                var prefix = hl[0];
                var content = hl.Length > 1 ? hl[1..] : "";

                if (prefix == ' ')
                {
                    if (ri >= result.Count || result[ri] != content)
                    {
                        // Try next line (some editors strip trailing whitespace)
                        if (ri + 1 < result.Count && result[ri + 1] == content)
                            ri++;
                        else
                        {
                            canApply = false;
                            break;
                        }
                    }
                    ri++;
                }
                else if (prefix == '-')
                {
                    if (ri < result.Count && result[ri] == content)
                    {
                        toRemove.Add(ri);
                        ri++;
                    }
                    else if (ri < result.Count && result[ri].TrimEnd() == content.TrimEnd())
                    {
                        toRemove.Add(ri);
                        ri++;
                    }
                    else
                    {
                        canApply = false;
                        break;
                    }
                }
                else if (prefix == '+')
                {
                    toInsert.Add((ri, content));
                }
            }

            if (!canApply)
            {
                failedHunks.Add(hi);
                continue;
            }

            // Apply: remove in reverse order, insert in order
            // Adjust indices based on prior insertions
            for (int i = toRemove.Count - 1; i >= 0; i--)
                result.RemoveAt(toRemove[i]);

            var insertOffset = 0;
            foreach (var (idx, line) in toInsert)
            {
                result.Insert(idx + insertOffset, line);
                insertOffset++;
            }

            applied++;
        }

        if (applied > 0)
        {
            await AtomicWriteAsync(fp, string.Join("\n", result) + "\n").ConfigureAwait(false);
        }

        var summary = $"Unified diff: {applied} hunk(s) applied";
        if (failedHunks.Count > 0)
            summary += $", {failedHunks.Count} hunk(s) failed (context mismatch)";
        return summary;
    }

    private static List<(int oldStart, List<string> lines)> ParseUnifiedHunks(string diff)
    {
        var hunks = new List<(int oldStart, List<string> lines)>();
        var diffLines = diff.Split('\n');
        List<string>? currentHunk = null;

        foreach (var line in diffLines)
        {
            var trimmed = line.TrimEnd('\r');

            // Skip git diff headers (diff --git, index, ---/+++ filename lines)
            if (trimmed.StartsWith("diff --git", StringComparison.Ordinal) ||
                trimmed.StartsWith("index ", StringComparison.Ordinal) ||
                trimmed.StartsWith("new file", StringComparison.Ordinal) ||
                trimmed.StartsWith("deleted file", StringComparison.Ordinal) ||
                trimmed.StartsWith("old mode", StringComparison.Ordinal) ||
                trimmed.StartsWith("new mode", StringComparison.Ordinal) ||
                trimmed.StartsWith("copy from", StringComparison.Ordinal) ||
                trimmed.StartsWith("copy to", StringComparison.Ordinal) ||
                trimmed.StartsWith("rename from", StringComparison.Ordinal) ||
                trimmed.StartsWith("rename to", StringComparison.Ordinal) ||
                trimmed.StartsWith("similarity index", StringComparison.Ordinal) ||
                (trimmed.StartsWith("--- ", StringComparison.Ordinal) && currentHunk == null) ||
                (trimmed.StartsWith("+++ ", StringComparison.Ordinal) && currentHunk == null))
                continue;

            if (trimmed.StartsWith("@@ ") && trimmed.Contains(" @@"))
            {
                if (currentHunk != null && currentHunk.Count > 0)
                {
                    var oldStart = ParseHunkStart(currentHunk[0]);
                    hunks.Add((oldStart, currentHunk.Skip(1).ToList()));
                }
                currentHunk = [trimmed];
            }
            else if (trimmed == @"\ No newline at end of file")
            {
                // Preserve marker in current hunk or skip if standalone
                if (currentHunk != null)
                    currentHunk.Add(trimmed);
            }
            else if (currentHunk != null && (trimmed.Length == 0 || trimmed[0] is ' ' or '-' or '+'))
            {
                currentHunk.Add(trimmed);
            }
        }

        if (currentHunk != null && currentHunk.Count > 1)
        {
            var oldStart = ParseHunkStart(currentHunk[0]);
            hunks.Add((oldStart, currentHunk.Skip(1).ToList()));
        }

        return hunks;
    }

    private static int ParseHunkStart(string header)
    {
        // @@ -old_start,old_count +new_start,new_count @@
        var m = Regex.Match(header, @"@@\s+-(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var start))
            return start;
        return 1;
    }

    // ========== DIFF ==========

    [Description("�Ƚ������ļ��������ı������ݲ��죬���� unified diff ��ʽ��\n"
        + "���ó������������Աȡ������ļ��ȶԡ��汾���������\n"
        + "�����ó�����Git �ύ��ıȽϣ����� GitDiff����\n"
        + "�ؼ�������leftPath �� ���ļ�·�����ı���rightPath �� ���ļ�·�����ı���")]
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
        catch (Exception ex) { return ToolResult.Error($"{ex.Message}"); }
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
            var isNew = !File.Exists(path);
            await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            EditLedger.Default.RecordEdit(path, isNew);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex_del)
            {
                System.Diagnostics.Debug.WriteLine($"[TextTools] Temp cleanup failed: {ex_del.Message}");
            }
            throw;
        }
    }

    private sealed record EditSpec(string Path, string Search, string Replace);

    private static List<string> ComputeLcs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        // ʹ�� 2 �й������飬���� O(m*n) �ڴ�
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
                curr[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
        }

        // �����ؽ� LCS���� prev ���� + ԭʼ���У�
        var result = new List<string>();
        int x = m, y = n;
        // �ؽ�ʱ��Ҫԭʼ dp ֵ�����¼��������������в���
        // ���� Hirschberg �㷨��ֱ����������ļ���С
        // ʵ�ʳ������ƣ������������� 1000 ʱ�ض�
        if (m > 1000 || n > 1000)
        {
            result.Add("...(diff too large, truncated)");
            return result;
        }
        // С�ļ�����������
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
