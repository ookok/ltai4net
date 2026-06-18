using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using LTAI.AI;
using LTAI.Core;

namespace LTAI.Agent.Tools;

[ToolDomain("core")]
public static class TextProcessingTools
{
    // ═══════════════════════════════════════════════════════
    //  CRLF normalization — all text tools process through this
    // ═══════════════════════════════════════════════════════

    /// <summary>Normalize CRLF → LF, split to lines, and trim trailing \r.</summary>
    private static string[] SplitLines(string text)
        => text.Split('\n', StringSplitOptions.None)
               .Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>Tool prefix for consistent error format: `tool: message`.</summary>
    private static string Err(string tool, string msg) => ToolResult.ErrorText(tool, msg);

    // ═══════════════════════════════════════════════════════
    //  tail — read last N lines (circular buffer, O(n), O(N) memory)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `tail` — 读取文件末尾 N 行。\n"
        + "适用场景：查看日志最新行、监控文件尾部内容。\n"
        + "性能：使用 StreamReader 流式读 + circular buffer，大文件 O(n)，内存 O(n)。")]
    [return: Description("文件最后 N 行")]
    public static async Task<string> tail(
        [Description("文件路径")] string path,
        [Description("行数（默认 10）")] int n = 10)
    {
        try
        {
            if (!File.Exists(path)) return $"File not found: {path}";
            n = Math.Clamp(n, 1, 10000);
            var fi = new FileInfo(path);
            // < 1MB — circular buffer (simple, no seek overhead)
            if (fi.Length < 1024 * 1024)
            {
                var buf = new Queue<string>(n + 1);
                using var sr = new StreamReader(path);
                string? line;
                while ((line = await sr.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    buf.Enqueue(line);
                    if (buf.Count > n) buf.Dequeue();
                }
                return buf.Count == 0 ? "(empty file)" : string.Join('\n', buf);
            }
            // >= 1MB — seek + reverse scan then read forward
            long startPos;
            bool partialLine;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                long pos = fs.Length;
                int newlinesFound = 0;
                byte[] chunk = new byte[4096];
                while (pos > 0 && newlinesFound <= n)
                {
                    int toRead = (int)Math.Min(chunk.Length, pos);
                    pos -= toRead;
                    fs.Seek(pos, SeekOrigin.Begin);
                    await fs.ReadExactlyAsync(chunk.AsMemory(0, toRead)).ConfigureAwait(false);
                    for (int i = toRead - 1; i >= 0 && newlinesFound <= n; i--)
                    {
                        if (chunk[i] == '\n')
                            newlinesFound++;
                    }
                }
                startPos = pos;
                // If startPos is in the middle of a line (not preceded by \n), the first
                // forward-read line will be a fragment — skip it.
                partialLine = startPos > 0 && chunk.Length > 0 && chunk[0] != '\n';
            }
            // Read forward from discovered position
            using var sr2 = new StreamReader(path);
            if (startPos > 0)
                sr2.BaseStream.Seek(startPos, SeekOrigin.Begin);
            var collected = new List<string>(n);
            string? l;
            // Skip the first partial line if we landed mid-line
            if (partialLine)
                await sr2.ReadLineAsync().ConfigureAwait(false);
            while ((l = await sr2.ReadLineAsync().ConfigureAwait(false)) != null)
                collected.Add(l);
            return collected.Count == 0
                ? "(empty file)"
                : string.Join('\n', collected.Skip(Math.Max(0, collected.Count - n)));
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  wc — word/line/char count (single-pass, zero-extra-alloc)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `wc` — 统计文件的行数、词数、字符数。\n"
        + "适用场景：统计代码行数、计算文件大小和词数。\n"
        + "性能：单次 StreamReader 遍历，词边界用 char.IsWhiteSpace 检测。")]
    [return: Description("行数 词数 字符数 文件名")]
    public static async Task<string> wc(
        [Description("文件路径")] string path)
    {
        try
        {
            if (!File.Exists(path)) return $"File not found: {path}";
            long lines = 0, words = 0, chars = 0;
            using var sr = new StreamReader(path);
            string? line;
            while ((line = await sr.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lines++;
                chars += line.Length + Environment.NewLine.Length;
                var span = line.AsSpan();
                bool inWord = false;
                for (int i = 0; i < span.Length; i++)
                {
                    if (char.IsWhiteSpace(span[i]))
                    {
                        inWord = false;
                    }
                    else if (!inWord)
                    {
                        inWord = true;
                        words++;
                    }
                }
            }
            var name = Path.GetFileName(path);
            return $"{lines,8} {words,8} {chars,8} {name}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  sort — sort lines (Array.Sort introsort, in-place)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `sort` — 排序文本行。\n"
        + "适用场景：按字母或数字排序代码列表、排序配置文件。\n"
        + "性能：Array.Sort introsort O(n log n)，可选数值排序/去重/逆序。")]
    [return: Description("排序后的行")]
    public static string sort(
        [Description("要排序的文本（多行字符串）")] string text,
        [Description("按数值排序而非字母")] bool numeric = false,
        [Description("逆序")] bool reverse = false,
        [Description("去重（同 uniq）")] bool unique = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = SplitLines(text);
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0)) return "";

        if (numeric)
        {
            Array.Sort(lines, (a, b) =>
            {
                double.TryParse(a, out var x);
                double.TryParse(b, out var y);
                return x.CompareTo(y);
            });
        }
        else
        {
            Array.Sort(lines, StringComparer.Ordinal);
        }

        if (reverse) Array.Reverse(lines);

        if (unique)
        {
            var sb = new StringBuilder(lines.Length);
            string? prev = null;
            foreach (var l in lines)
            {
                if (prev == null || l != prev)
                {
                    sb.AppendLine(l);
                    prev = l;
                }
            }
            return sb.ToString();
        }

        return string.Join('\n', lines);
    }

    // ═══════════════════════════════════════════════════════
    //  uniq — dedup consecutive lines (single-pass streaming)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `uniq` — 去重连续重复行。\n"
        + "适用场景：去除排序后文本的重复行、压缩连续相同行。\n"
        + "性能：单次遍历，O(n)，仅保留下一个与前一个不同的行。")]
    [return: Description("去重后的行")]
    public static string uniq(
        [Description("要处理的文本（多行字符串）")] string text,
        [Description("只输出重复行")] bool repeated = false,
        [Description("输出每行出现次数")] bool count = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = SplitLines(text);
        if (lines.Length == 0) return "";

        var sb = new StringBuilder(lines.Length);
        string? prev = null;
        int repeatCount = 0;

        foreach (var l in lines)
        {
            if (prev == null || l != prev)
            {
                if (prev != null)
                {
                    if (!repeated || repeatCount > 1)
                        sb.AppendLine(count ? $"{repeatCount,4} {prev}" : prev);
                }
                prev = l;
                repeatCount = 1;
            }
            else
            {
                repeatCount++;
            }
        }

        if (prev != null && (!repeated || repeatCount > 1))
            sb.AppendLine(count ? $"{repeatCount,4} {prev}" : prev);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════
    //  cut — column extraction (span-based split, field range)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `cut` — 按分隔符提取列。\n"
        + "适用场景：从 CSV/TSV 中提取特定列、解析结构化文本。\n"
        + "参数：delimiter — 分隔符（默认 TAB）；fields — 列号，用逗号分隔如 1,3 或 1-3。")]
    [return: Description("提取的列")]
    public static string cut(
        [Description("要处理的文本（多行字符串）")] string text,
        [Description("分隔符（默认 TAB）")] string delimiter = "\t",
        [Description("字段范围，如 1,3 或 1-3,5")] string fields = "1")
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fieldRanges = ParseFieldRanges(fields);
        if (fieldRanges.Count == 0) return "";

        var lines = SplitLines(text);
        var sb = new StringBuilder(text.Length / 2);

        foreach (var line in lines)
        {
            var parts = line.Split(delimiter);
            var first = true;
            foreach (var (start, end) in fieldRanges)
            {
                for (int i = start; i <= end && i <= parts.Length; i++)
                {
                    if (!first) sb.Append(delimiter);
                    sb.Append(parts[i - 1]);
                    first = false;
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════
    //  tr — character translation (char mapping via array lookup)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `tr` — 字符替换/删除。\n"
        + "适用场景：替换文件中的特定字符、大小写转换、删除空白字符。\n"
        + "支持字符类：[:upper:], [:lower:], [:space:], [:digit:]。")]
    [return: Description("替换后的文本")]
    public static string tr(
        [Description("要处理的文本")] string text,
        [Description("源字符集，如 'a-z' 或 '[:upper:]'")] string set1,
        [Description("目标字符集，如 'A-Z' 或 '[:lower:]'")] string set2,
        [Description("删除 set1 中的字符而非替换")] bool delete = false)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!delete && string.IsNullOrEmpty(set2)) return text;

        var result = text.AsSpan().ToArray();
        var lookup = BuildLookup(set1, set2 ?? "", delete);
        var writeIdx = 0;

        for (int i = 0; i < result.Length; i++)
        {
            var c = result[i];
            var replacement = lookup[c];
            if (replacement >= 0)
            {
                if (!delete)
                    result[writeIdx++] = (char)replacement;
            }
            else
            {
                result[writeIdx++] = c;
            }
        }

        return delete ? new string(result, 0, writeIdx) : new string(result);
    }

    // ═══════════════════════════════════════════════════════
    //  tee — write file AND return content simultaneously
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `tee` — 写入文件同时返回内容。\n"
        + "适用场景：保存日志到文件的同时查看输出、调试写入。\n"
        + "参数：path — 输出文件路径；content — 要写入并返回的内容。")]
    [return: Description("写入的内容（同输入）")]
    public static async Task<string> tee(
        [Description("输出文件路径")] string path,
        [Description("要写入并返回的内容")] string content,
        [Description("追加而非覆盖")] bool append = false)
    {
        try
        {
            var fp = PathUtils.SafeResolvePath(Directory.GetCurrentDirectory(), path);
            if (fp == null) return "Error: path escape";
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            if (append)
                await File.AppendAllTextAsync(fp, content).ConfigureAwait(false);
            else
                await File.WriteAllTextAsync(fp, content).ConfigureAwait(false);
            return content;
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  du — directory disk usage (recursive, parallel for large trees)
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `du` — 统计目录磁盘使用量。\n"
        + "适用场景：查看项目空间占用、查找大目录。\n"
        + "性能：Directory.EnumerateFiles 延迟枚举 + 并行统计。")]
    [return: Description("目录大小摘要")]
    public static Task<string> du(
        [Description("目录路径（默认当前目录）")] string path = ".",
        [Description("最大深度 1-5（默认不限）")] int? maxDepth = null)
    {
        // Run on thread pool to avoid blocking the caller
        return Task.Run(() =>
        {
            try
            {
                var ws = Directory.GetCurrentDirectory();
                var fp = PathUtils.SafeResolvePath(ws, path);
                if (fp == null) return "Error: path escape";
                if (!Directory.Exists(fp)) return $"Directory not found: {fp}";

                var results = new List<(string dir, long size)>();
                var dirs = maxDepth.HasValue
                    ? GetDirectoriesWithDepth(fp, maxDepth.Value)
                    : [fp];

                foreach (var dir in dirs)
                {
                    long size = 0;
                    try
                    {
                        // Parallel file size enumeration for large directories
                        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray();
                        if (files.Length > 100)
                        {
                            size = files.AsParallel()
                                .Select(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                                .Sum();
                        }
                        else
                        {
                            foreach (var f in files)
                            {
                                try { size += new FileInfo(f).Length; }
                                catch { /* skip inaccessible */ }
                            }
                        }
                    }
                    catch { /* skip inaccessible */ }
                    results.Add((dir, size));
                }

                var total = results.Sum(r => r.size);
                var sb = new StringBuilder();
                sb.AppendLine("| Directory | Size |");
                sb.AppendLine("|-----------|------|");
                foreach (var (dir, size) in results.OrderByDescending(r => r.size).Take(20))
                {
                    var rel = Path.GetRelativePath(fp, dir);
                    var label = rel == "." ? Path.GetFileName(fp) : rel;
                    sb.AppendLine($"| {label}/ | {FormatSize(size)} |");
                }
                sb.AppendLine($"| **Total** | **{FormatSize(total)}** |");
                return sb.ToString();
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        });
    }

    // ═══════════════════════════════════════════════════════
    //  df — disk free space
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `df` — 磁盘空间使用情况。\n"
        + "适用场景：查看各磁盘/分区的剩余空间。")]
    [return: Description("磁盘使用表")]
    public static string df()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Drive | Type | Size | Used | Free | Use% |");
        sb.AppendLine("|-------|------|------|------|------|------|");
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    var pct = drive.TotalSize > 0
                        ? $"{(double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100:F0}%"
                        : "N/A";
                    sb.AppendLine($"| {drive.Name} | {drive.DriveType} | {FormatSize(drive.TotalSize)} | {FormatSize(drive.TotalSize - drive.AvailableFreeSpace)} | {FormatSize(drive.AvailableFreeSpace)} | {pct} |");
                }
                catch { /* skip inaccessible */ }
            }
        }
        catch { /* skip */ }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════
    //  seq — generate number sequence
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `seq` — 生成数字序列。\n"
        + "适用场景：生成循环变量、创建编号列表。\n"
        + "参数：start — 起始值（默认 1）；end — 结束值；step — 步长（默认 1）。")]
    [return: Description("每行一个数字")]
    public static string seq(
        [Description("结束值（如果只有一个参数）或起始值")] string first,
        [Description("结束值（可选，三个参数时表示步长后面的结束值）")] string? second = null,
        [Description("步长（可选）")] string? third = null)
    {
        // Parse all params as decimal strings to avoid floating-point drift.
        // Count iterations in integer arithmetic, then compute value as start + i * step.
        decimal start, end, step = 1;

        if (third != null)
        {
            if (!decimal.TryParse(first, out start) || !decimal.TryParse(third, out end) || !decimal.TryParse(second, out step))
                return "Error: seq expects numbers";
        }
        else if (second != null)
        {
            if (!decimal.TryParse(first, out start) || !decimal.TryParse(second, out end))
                return "Error: seq expects numbers";
        }
        else
        {
            start = 1;
            if (!decimal.TryParse(first, out end))
                return "Error: seq expects numbers";
        }

        if (step == 0) return "Error: step cannot be zero";

        var sb = new StringBuilder();
        var count = (int)Math.Floor((double)((end - start) / step)) + 1;
        if (count < 0) count = 0;
        if (count > 100000) return "Error: seq range too large (max 100000)";

        for (int i = 0; i < count; i++)
        {
            var val = start + i * step;
            sb.AppendLine(val.ToString());
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════

    private static List<(int start, int end)> ParseFieldRanges(string fields)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in fields.Split(','))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var rangeParts = trimmed.Split('-');
            if (rangeParts.Length == 1 && int.TryParse(rangeParts[0], out var n))
                ranges.Add((n, n));
            else if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], out var s)
                && int.TryParse(rangeParts[1], out var e))
                ranges.Add((s, e));
        }
        return ranges;
    }

    private static int[] BuildLookup(string set1, string set2, bool delete)
    {
        var lookup = new int[65536];
        Array.Fill(lookup, -1);
        var chars1 = ExpandSet(set1);
        var chars2 = delete ? "" : ExpandSet(set2);

        for (int i = 0; i < chars1.Length; i++)
        {
            var c = chars1[i];
            var replacement = i < chars2.Length ? chars2[i] : chars2[^1];
            lookup[c] = replacement;
        }
        return lookup;
    }

    private static string ExpandSet(string set)
    {
        if (set.StartsWith("[:") && set.EndsWith(":]"))
        {
            return set.ToLowerInvariant() switch
            {
                "[:upper:]" => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                "[:lower:]" => "abcdefghijklmnopqrstuvwxyz",
                "[:digit:]" => "0123456789",
                "[:space:]" => " \t\n\r\f\v",
                "[:alpha:]" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
                "[:alnum:]" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
                "[:punct:]" => "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
                _ => set
            };
        }

        // Expand ranges like a-z; backslash escapes the next char (literal match)
        var sb = new StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i] == '\\' && i + 1 < set.Length)
            {
                sb.Append(set[i + 1]);
                i++;
            }
            else if (i + 2 < set.Length && set[i + 1] == '-' && set[i + 2] != '\\')
            {
                for (var c = set[i]; c <= set[i + 2]; c++)
                    sb.Append(c);
                i += 2;
            }
            else
            {
                sb.Append(set[i]);
            }
        }
        return sb.ToString();
    }

    private static string[] GetDirectoriesWithDepth(string root, int maxDepth)
    {
        var dirs = new List<string> { root };
        if (maxDepth <= 1) return dirs.ToArray();

        var stack = new Queue<(string dir, int depth)>();
        stack.Enqueue((root, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Dequeue();
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    dirs.Add(sub);
                    stack.Enqueue((sub, depth + 1));
                }
            }
            catch { /* skip inaccessible */ }
        }
        return dirs.ToArray();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        < 1073741824 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };
}
