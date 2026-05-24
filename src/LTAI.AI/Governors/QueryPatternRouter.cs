using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record PatternMatchResult
{
    public bool Matched { get; init; }
    public string? ToolName { get; init; }
    public Dictionary<string, object?>? ToolArgs { get; init; }
    public string? ContextMessage { get; init; }
    public float Confidence { get; init; }
    public string? ExtractedTarget { get; init; }

    public static PatternMatchResult NoMatch => new() { Matched = false };
}

public sealed class QueryPatternRouter
{
    private readonly AIToolRegistry _toolRegistry;
    private readonly ILogger<QueryPatternRouter>? _logger;

    private sealed record PatternEntry(
        string Name, Regex Regex, string ToolName,
        Func<Match, string, Dictionary<string, object?>> BuildArgs,
        Func<Match, string, string> BuildContext,
        float Confidence);

    private readonly List<PatternEntry> _patterns;
    private const int DefaultMaxSearchResults = 5;

    public QueryPatternRouter(AIToolRegistry toolRegistry, ILogger<QueryPatternRouter>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;

        _patterns = new List<PatternEntry>
        {
            // Priority 1: File/directory listing (most specific)
            new("list_files",
                new Regex(@"列出.*(?:文件|目录|内容|文件夹)|显示.*(?:文件|目录)|(?:有什么|有哪些).*文件|(?:ls|dir)\b",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "filesystem_list",
                (m, q) =>
                {
                    return new Dictionary<string, object?> { ["path"] = ExtractDirPath(q), ["pattern"] = null! };
                },
                (m, q) => "以下是通过 filesystem_list 工具获取的目录文件列表（JSON格式，type=dir/folder 表示目录，type=file 表示文件），请提取其中的文件名据实描述：",
                1.0f),

            // Priority 1b: Compare/count files in directories
            new("compare_dirs",
                new Regex(@"对比|比较|哪个.*多|哪个.*少|各有多少|计数|统计.*文件|count.*files?|多少个.*文件",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "shell_exec",
                (m, q) =>
                {
                    return new Dictionary<string, object?>
                    {
                        ["command"] = BuildComparisonCommand(q),
                        ["workingDirectory"] = null!
                    };
                },
                (m, q) => "以下是通过命令自动统计的目录文件数量，请据实描述，不得编造数字：",
                0.95f),

            // Priority 2: File reading
            new("read_file",
                new Regex(@"读取|查看.*内容|打开.*文件|cat\s+|read.*file|读.*文件",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "filesystem_read",
                (m, q) =>
                {
                    // Extract filename: "读取 xxx", "查看 xxx 的内容", "cat xxx"
                    var pathMatch = Regex.Match(q, @"(?:读取|查看|打开|cat|read)\s+['\""]?([^\s'""，,。？?]+)");
                    var path = pathMatch.Success ? pathMatch.Groups[1].Value : "";
                    return new Dictionary<string, object?> { ["path"] = path };
                },
                (m, q) => "以下是通过 filesystem_read 工具读取的文件内容，请严格基于该内容回答：",
                0.95f),

            // Priority 3: Web search
            new("web_search",
                new Regex(@"搜索|查找|帮我查|查一下|百度|用.*搜|search\s+",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "web_search",
                (m, q) =>
                {
                    var target = ExtractSearchTarget(q);
                    return new Dictionary<string, object?>
                    {
                        ["query"] = target,
                        ["maxResults"] = DefaultMaxSearchResults
                    };
                },
                (m, q) => $"以下是通过 web_search 自动搜索 \"{ExtractSearchTarget(q)}\" 获得的原始搜索结果，请严格基于这些结果总结回答，不得添加任何搜索结果中不存在的信息：",
                0.9f),

            // Priority 4: Git diff
            new("git_diff",
                new Regex(@"git\s*diff|查看.*(?:变更|改动|修改)|有什么.*(?:改动|变化|变更)|修改了.*什么|what.*changed",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "git_diff",
                (m, q) => new Dictionary<string, object?>
                {
                    ["repoPath"] = null!,
                    ["files"] = null!,
                    ["staged"] = false
                },
                (m, q) => "以下是通过 git diff 获取的当前仓库变更，请据实描述，不得编造：",
                0.95f),

            // Priority 5: Git log
            new("git_log",
                new Regex(@"git\s*log|提交.*记录|commit.*(?:log|history)|最近.*提交|commit\s+history",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "git_log",
                (m, q) => new Dictionary<string, object?>
                {
                    ["repoPath"] = null!,
                    ["maxCount"] = 10,
                    ["format"] = "oneline"
                },
                (m, q) => "以下是通过 git log 获取的最近提交记录，请据实描述，不得编造：",
                0.95f),

            // Priority 6: System info
            new("sysinfo",
                new Regex(@"系统.*信息|环境.*信息|sysinfo|操作系统|系统.*配置|什么.*系统|what.*(?:os|system|platform)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "env_sysinfo",
                (m, q) => new Dictionary<string, object?>(),
                (m, q) => "以下是通过 env_sysinfo 获取的系统信息，请据实描述，不得编造：",
                0.95f),

            // Priority 7: Date/time (explicit tool request)
            new("datetime",
                new Regex(@"^(?:现在|当前).*(?:时间|日期|几点)|what.*(?:time|date).*now|current.*(?:time|date)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "datetime_now",
                (m, q) => new Dictionary<string, object?> { ["timezoneOffset"] = null! },
                (m, q) => "以下是通过 datetime_now 获取的当前时间，请据实描述：",
                1.0f),

            // Priority 8: Environment processes
            new("processes",
                new Regex(@"进程|process|运行.*程序|running.*process|ps\s",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "env_processes",
                (m, q) =>
                {
                    var filter = ExtractTargetAfterKeyword(q, "进程|process");
                    return new Dictionary<string, object?>
                    {
                        ["filter"] = filter ?? null!,
                        ["top"] = 20
                    };
                },
                (m, q) => "以下是通过 env_processes 获取的进程列表，请据实描述：",
                0.9f),

            // Priority 9: Network info
            new("network",
                new Regex(@"网络.*信息|ip.*地址|ping|network.*info|网络.*状态",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "env_network",
                (m, q) =>
                {
                    var host = ExtractTargetAfterKeyword(q, @"ping\s+|网络.*(?:到|连接|访问)");
                    return new Dictionary<string, object?> { ["pingHost"] = host ?? null! };
                },
                (m, q) => "以下是通过 env_network 获取的网络信息，请据实描述：",
                0.9f),

            // Priority 10: Shell environment
            new("shell_env",
                new Regex(@"当前.*目录|工作.*目录|pwd|working.*dir|在.*哪个.*目录|在.*什么.*目录|cwd",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "shell_env",
                (m, q) => new Dictionary<string, object?>(),
                (m, q) => "以下是通过 shell_env 获取的工作目录和环境信息：",
                0.95f),

            // Priority 11: Code impact analysis
            new("code_impact",
                new Regex(@"理解.*(?:代码|变更|diff)|分析.*影响|影响.*分析|understand.*(?:code|diff)|analyze.*impact",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "understand_diff",
                (m, q) => new Dictionary<string, object?> { ["repoPath"] = null! },
                (m, q) => "以下是通过 understand_diff 分析的代码影响：",
                0.85f),
        };
    }

    public async Task<PatternMatchResult> MatchAndExecuteAsync(
        string query, CancellationToken cancellationToken = default)
    {
        foreach (var pattern in _patterns)
        {
            var match = pattern.Regex.Match(query);
            if (!match.Success) continue;

            if (!_toolRegistry.HasTool(pattern.ToolName))
            {
                _logger?.LogDebug("Pattern '{Pattern}' matched but tool '{Tool}' not available",
                    pattern.Name, pattern.ToolName);
                continue;
            }

            var args = pattern.BuildArgs(match, query);
            var contextMsg = pattern.BuildContext(match, query);

            try
            {
                var result = await _toolRegistry.InvokeAsync(pattern.ToolName, args, cancellationToken);
                var resultText = result?.ToString() ?? "";

                _logger?.LogInformation("Layer1 auto-tool: {Pattern} → {Tool} → {Length} chars",
                    pattern.Name, pattern.ToolName, resultText.Length);

                if (string.IsNullOrWhiteSpace(resultText))
                {
                    return new PatternMatchResult
                    {
                        Matched = true,
                        ToolName = pattern.ToolName,
                        ToolArgs = args,
                        ContextMessage = $"{contextMsg}\n(工具返回了空结果)",
                        Confidence = pattern.Confidence,
                        ExtractedTarget = GetExtractedTarget(pattern, query)
                    };
                }

                var truncated = resultText.Length > 4000 ? resultText[..4000] : resultText;
                var formatted = FormatToolResult(pattern.Name, truncated, pattern.ToolName);

                return new PatternMatchResult
                {
                    Matched = true,
                    ToolName = pattern.ToolName,
                    ToolArgs = args,
                    ContextMessage = $"{contextMsg}\n{formatted}",
                    Confidence = pattern.Confidence,
                    ExtractedTarget = GetExtractedTarget(pattern, query)
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Layer1 auto-tool {Tool} failed: {Error}", pattern.ToolName, ex.Message);
                return new PatternMatchResult
                {
                    Matched = true,
                    ToolName = pattern.ToolName,
                    ToolArgs = args,
                    ContextMessage = $"{contextMsg}\n(工具执行失败: {ex.Message})",
                    Confidence = 0.3f,
                    ExtractedTarget = GetExtractedTarget(pattern, query)
                };
            }
        }

        return PatternMatchResult.NoMatch;
    }

    private static string ExtractDirPath(string query)
    {
        foreach (var keyword in new[] { "目录", "文件夹", "路径" })
        {
            var idx = query.IndexOf(keyword, StringComparison.Ordinal);
            if (idx <= 0) continue;

            var before = query[..idx];
            before = Regex.Replace(before, @"(列出|显示|查看|在|当前|这个|此|本|该|工作|的|从|到)\s*", "");
            before = before.Trim();

            if (!string.IsNullOrEmpty(before) && before.Length <= 80
                && !before.Contains("文件") && !before.Contains("内容"))
                return before;
        }
        return ".";
    }

    private static string BuildComparisonCommand(string query)
    {
        var dirPattern = Regex.Matches(query, @"([a-zA-Z0-9_\-\.\\/]+|[^\s，,。！？\u201c-\u201d]{1,30})(?:目录|文件夹|路径|下)");
        var dirs = new List<string>();
        foreach (Match m in dirPattern)
        {
            var d = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(d) && d.Length <= 30
                && d is not ("列出" or "显示" or "查看" or "当前" or "这个" or "此" or "本" or "该" or "的" or "对比" or "比较"))
                dirs.Add(d);
        }

        if (dirs.Count >= 2)
        {
            dirs = dirs.Distinct().Take(3).ToList();
            var parts = dirs.Select(d =>
                $"powershell -Command \"Write-Host '{d}:'; (Get-ChildItem -Path {d} -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count\"");
            return string.Join(" & ", parts);
        }

        return "powershell -Command \"$d='src'; Write-Host 'src:'; (Get-ChildItem -Path $d -File -Recurse -ErrorAction SilentlyContinue|Measure-Object).Count; $d='tests'; Write-Host 'tests:'; (Get-ChildItem -Path $d -File -Recurse -ErrorAction SilentlyContinue|Measure-Object).Count; $d='docs'; Write-Host 'docs:'; (Get-ChildItem -Path $d -File -Recurse -ErrorAction SilentlyContinue|Measure-Object).Count; $d='config'; Write-Host 'config:'; (Get-ChildItem -Path $d -File -Recurse -ErrorAction SilentlyContinue|Measure-Object).Count\"";
    }

    private static string ExtractSearchTarget(string query)
    {
        var patterns = new[] {
            @"搜索\s*(.+?)(?:\s*[，,。！？]|$)",
            @"查找\s*(.+?)(?:\s*[，,。！？]|$)",
            @"帮我查\s*(.+?)(?:\s*[，,。！？]|$)",
            @"查一下\s*(.+?)(?:\s*[，,。！？]|$)",
            @"百度\s*(.+?)(?:\s*[，,。！？]|$)",
            @"用.*搜[sS]earch\s*(.+?)(?:\s*[，,。！？]|$)",
            @"search\s+(.+?)(?:\s*$)",
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(query, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var target = m.Groups[1].Value.Trim();
                return target.Length > 0 ? target : query.Trim();
            }
        }

        return query.Trim();
    }

    private static string? ExtractTargetAfterKeyword(string query, string keywordPattern)
    {
        var m = Regex.Match(query, $@"{keywordPattern}\s*(.+?)(?:\s*[，,。！？]|$)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? GetExtractedTarget(PatternEntry pattern, string query)
    {
        return pattern.Name switch
        {
            "web_search" => ExtractSearchTarget(query),
            _ => null
        };
    }

    private static string FormatToolResult(string patternName, string rawResult, string toolName)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResult);
            var root = doc.RootElement;

            return patternName switch
            {
                "list_files" => FormatFileList(root),
                "read_file" => FormatFileContent(root),
                "shell_env" => FormatShellEnv(root),
                "sysinfo" => FormatSysInfo(root),
                "processes" => FormatProcesses(root),
                "network" => FormatNetwork(root),
                "datetime" => FormatDateTime(root),
                _ => rawResult
            };
        }
        catch
        {
            return rawResult;
        }
    }

    private static string FormatFileList(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
            return $"错误: {err.GetString()}";

        if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            return "(目录为空)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"目录: {root.GetProperty("path").GetString()}");
        sb.AppendLine($"共 {root.GetProperty("count").GetInt32()} 个条目:");

        foreach (var item in items.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            var name = item.GetProperty("name").GetString();
            var prefix = type == "dir" ? "  [目录] " : "  [文件] ";
            sb.Append(prefix).Append(name);

            if (type == "file" && item.TryGetProperty("size", out var size))
                sb.Append($" ({FormatSize(size.GetInt64())})");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatFileContent(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
            return $"错误: {err.GetString()}";

        var path = root.GetProperty("path").GetString();
        var content = root.GetProperty("content").GetString();
        var maxLen = 3000;
        if (content.Length > maxLen)
            content = content[..maxLen] + $"\n... (截断，共 {content.Length} 字符)";
        return $"文件: {path}\n\n{content}";
    }

    private static string FormatShellEnv(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
            return $"错误: {err.GetString()}";
        return root.TryGetProperty("currentDirectory", out var cwd)
            ? $"当前工作目录: {cwd.GetString()}"
            : rawResult(root);
    }

    private static string FormatSysInfo(JsonElement root)
    {
        var sb = new System.Text.StringBuilder();
        if (root.TryGetProperty("os", out var os)) sb.AppendLine($"操作系统: {os}");
        if (root.TryGetProperty("osVersion", out var osv)) sb.AppendLine($"版本: {osv}");
        if (root.TryGetProperty("machineName", out var mn)) sb.AppendLine($"主机名: {mn}");
        if (root.TryGetProperty("processorCount", out var pc)) sb.AppendLine($"CPU核心数: {pc}");
        if (root.TryGetProperty("totalMemoryMB", out var tm)) sb.AppendLine($"总内存: {tm} MB");
        return sb.Length > 0 ? sb.ToString().TrimEnd() : rawResult(root);
    }

    private static string FormatProcesses(JsonElement root)
    {
        if (!root.TryGetProperty("processes", out var procs))
            return rawResult(root);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"进程数: {root.GetProperty("count").GetInt32()}");
        foreach (var p in procs.EnumerateArray().Take(20))
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var pid = p.TryGetProperty("id", out var id) ? id.GetInt32().ToString() : "?";
            sb.AppendLine($"  PID {pid}: {name}");
        }
        return sb.ToString();
    }

    private static string FormatNetwork(JsonElement root)
    {
        var sb = new System.Text.StringBuilder();
        if (root.TryGetProperty("hostname", out var h)) sb.AppendLine($"主机名: {h}");
        if (root.TryGetProperty("ipAddress", out var ip)) sb.AppendLine($"IP地址: {ip}");
        if (root.TryGetProperty("pingResult", out var pr)) sb.AppendLine($"Ping结果: {pr}");
        return sb.Length > 0 ? sb.ToString().TrimEnd() : rawResult(root);
    }

    private static string FormatDateTime(JsonElement root)
    {
        if (root.TryGetProperty("dateTime", out var dt)) return dt.GetString() ?? rawResult(root);
        if (root.TryGetProperty("iso", out var iso)) return iso.GetString() ?? rawResult(root);
        return rawResult(root);
    }

    private static string rawResult(JsonElement root) => root.ToString();

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1}MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB"
        };
    }
}
