using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Core.Configuration;

namespace LTAI.Agent.Format;

public static class BabelTeleFormatter
{
    private static readonly ConcurrentDictionary<string, bool> s_seenTypes = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForContext() => s_seenTypes.Clear();

    public static string EncodeToolResult(string toolName, string args, string result, int seq)
    {
        var type = GetToolType(toolName);
        var firstUse = s_seenTypes.TryAdd(type, true);
        var sb = new StringBuilder();
        if (firstUse)
            sb.AppendLine(GetExpansion(type));

        var summary = SummarizeResult(toolName, result);
        var compact = $"[{type}:{Sanitize(toolName)}#{seq}]";
        if (summary != null)
            compact += $" {summary}";

        var naiveTokens = result.Length / 4;
        var actualTokens = compact.Length / 3 + 1;
        TokenSavingsTracker.RecordLookup(naiveTokens, actualTokens);

        sb.Append(compact);
        return sb.ToString();
    }

    public static string EncodeSearchResult(string pattern, int matchCount, string? firstFile, int firstLine)
    {
        var firstUse = s_seenTypes.TryAdd("S", true);
        var sb = new StringBuilder();
        if (firstUse)
            sb.AppendLine(GetExpansion("S"));

        sb.Append($"[S:{Sanitize(pattern)}) m={matchCount}");
        if (firstFile != null)
        {
            var f = CompactPath(firstFile);
            sb.Append($" {f}:L{firstLine}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string EncodeRef(string filePath, int line, double? relevance = null)
    {
        var firstUse = s_seenTypes.TryAdd("R", true);
        var sb = new StringBuilder();
        if (firstUse)
            sb.AppendLine(GetExpansion("R"));

        sb.Append($"[R:{CompactPath(filePath)}#L{line}");
        if (relevance.HasValue)
            sb.Append($" r={relevance.Value:F2}");
        sb.Append(']');
        return sb.ToString();
    }

    public static string EncodeError(string code, int line, int col, string? message = null)
    {
        var firstUse = s_seenTypes.TryAdd("E", true);
        var sb = new StringBuilder();
        if (firstUse)
            sb.AppendLine(GetExpansion("E"));

        sb.Append($"[E:{code} L{line}:{col}");
        if (message != null && message.Length < 60)
            sb.Append($" {Sanitize(message)}");
        sb.Append(']');
        return sb.ToString();
    }

    public static string EncodeGraphResult(string query, int nodeCount, string? compactLines = null)
    {
        var firstUse = s_seenTypes.TryAdd("G", true);
        var sb = new StringBuilder();
        if (firstUse)
            sb.AppendLine(GetExpansion("G"));

        sb.Append($"[G:{Sanitize(Truncate(query, 40))} n={nodeCount}");
        if (compactLines != null)
        {
            sb.Append(' ');
            sb.Append(compactLines);
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string ExpansionHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## BabelTele 紧凑格式说明");
        sb.AppendLine("以下格式为 LLM 对 LLM 的紧凑编码，人类可读性低但语义完整。");
        sb.AppendLine("- [T:tool#N]: 工具结果（工具名#序号 [摘要]）");
        sb.AppendLine("- [G:query n=N]: 图查询结果（查询词 节点数 编码行）");
        sb.AppendLine("- [S:pattern m=N file:Ln]: 搜索结果（模式 匹配数 文件:行）");
        sb.AppendLine("- [R:path#Ln r=N]: 文件引用（路径#行号 相关度）");
        sb.AppendLine("- [E:code Ln:c]: 错误（错误码 行:列 [消息]）");
        sb.AppendLine("首次出现的类型附带展开说明，后续仅用紧凑引用。");
        return sb.ToString();
    }

    private static string GetExpansion(string type) => type switch
    {
        "T" => "## [T:tool] = 紧凑工具结果 [T:工具名#序号 摘要]\n",
        "G" => "## [G:graph] = 紧凑图查询 [G:查询词 n=节点数 类型:名@路径]\n",
        "S" => "## [S:search] = 紧凑搜索 [S:模式 m=匹配数 文件:L行]\n",
        "R" => "## [R:ref] = 紧凑引用 [R:路径#行号 r=相关度]\n",
        "E" => "## [E:err] = 紧凑错误 [E:错误码 L行:列 消息]\n",
        _ => "",
    };

    private static string GetToolType(string toolName)
    {
        var lower = toolName.ToLowerInvariant();
        if (lower is "readfilecontent" or "readfile" or "read"
            or "searchcontent" or "search" or "glob") return "T";
        if (lower is "runcommand" or "safeshell" or "exec") return "T";
        if (lower is "writefile" or "write" or "edit" or "applypatches") return "T";
        return "T";
    }

    private static string? SummarizeResult(string toolName, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        var trimmed = result.TrimStart();
        var firstLine = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine == null) return null;
        var truncated = Truncate(firstLine, 80);
        return truncated.Length < result.Length ? truncated + "…" : truncated;
    }

    private static string CompactPath(string path)
    {
        if (path.Length <= 40) return path;
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return path;
        return $"{parts[^3]}/{parts[^2]}/{parts[^1]}";
    }

    private static string Sanitize(string s)
    {
        return s.Replace('\n', ' ').Replace('\r', ' ').Replace('[', '(').Replace(']', ')');
    }

    private static string Truncate(string s, int maxLen)
    {
        return s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";
    }
}
