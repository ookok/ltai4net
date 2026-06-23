using System.ComponentModel;
using System.Text;
using LTAI.AI;
using LTAI.Agent.Caching;

namespace LTAI.Agent.Tools;

[ToolDomain("explore")]
[ToolPermission(ToolPermission.Read)]
public sealed class ExploreToolSet
{
    private readonly FileSystemTools _fs;
    private readonly SearchTools _search;
    private readonly string _ws;

    public ExploreToolSet(string ws, MmapFileProvider? mmap = null, WriteBuffer? writeBuf = null)
    {
        _ws = ws;
        _fs = new FileSystemTools(ws, mmap, writeBuf);
        _search = new SearchTools(ws);
    }

    [ReadOnlyTool]
    [Description("读取文件并返回紧凑引用 (path:start-end)。\n"
        + "适用场景：查看源代码、检查特定行范围的内容。\n"
        + "关键参数：path — 文件路径；startLine/endLine — 可选行范围。")]
    [return: Description("紧凑引用的文件内容")]
    public async Task<string> ReadCite(
        string path,
        [Description("起始行号（从 1 开始）")] int startLine = 1,
        [Description("结束行号（默认文件末尾）")] int? endLine = null)
    {
        var content = await _fs.ReadFileContent(path, startLine, endLine, maxChars: 0)
            .ConfigureAwait(false);
        if (content.StartsWith("Path '") || content.StartsWith("Error"))
            return content;

        var relPath = GetRelativePath(path);
        var lineCount = content.Count(c => c == '\n') + 1;
        var end = endLine ?? startLine + lineCount - 1;
        return $"<citation path=\"{relPath}\" lines=\"{startLine}-{end}\">\n{content}\n</citation>";
    }

    [ReadOnlyTool]
    [Description("搜索文件名（glob 模式），返回紧凑路径列表。\n"
        + "适用场景：按文件名模式定位文件、查找特定类型的文件。\n"
        + "关键参数：pattern — glob 模式（如 **/*.cs）；dir — 搜索起始目录。")]
    [return: Description("匹配文件的紧凑路径列表")]
    public string Glob(
        [Description("Glob 模式，如 **/*.cs 或 src/**/handler*.ts")] string pattern,
        [Description("搜索起始目录（默认为当前目录）")] string dir = ".",
        [Description("最大返回数量")] int limit = 200)
    {
        var results = _fs.Glob(pattern, dir, limit: limit);
        if (results.Length == 0)
            return "<file-list empty=\"true\"/>";
        var sb = new StringBuilder();
        sb.AppendLine("<file-list>");
        foreach (var r in results)
            sb.AppendLine($"  <file>{r}</file>");
        sb.Append("</file-list>");
        return sb.ToString();
    }

    [ReadOnlyTool]
    [Description("搜索文件内容（grep），返回紧凑的 file:line 引用。\n"
        + "适用场景：查找某个函数/类/变量的所有引用位置、搜索错误消息来源。\n"
        + "关键参数：pattern — 正则表达式；glob — 文件过滤模式（默认所有文件）。")]
    [return: Description("匹配行的 <citation> 引用块")]
    public async Task<string> SearchCompact(
        [Description("正则表达式模式，如 \"class\\s+\\w+\" 或 \"HttpClient\"")] string pattern,
        [Description("文件过滤 glob，如 *.cs 或 src/**/*.ts")] string glob = "*",
        [Description("匹配行前后附加上下文行数")] int context = 0,
        [Description("是否区分大小写")] bool caseSensitive = false)
    {
        var raw = await _search.SearchContent(pattern, glob, context, caseSensitive)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw) || raw.StartsWith("Error"))
            return raw ?? "<search-result empty=\"true\"/>";
        return FormatSearchResults(raw);
    }

    [ReadOnlyTool]
    [Description("列出目录内容。")]
    [return: Description("目录条目数组")]
    public string[] ListDir(
        [Description("目录路径")] string path = ".")
        => _fs.ListFiles(path);

    [ReadOnlyTool]
    [Description("显示目录树结构。")]
    [return: Description("目录树文本")]
    public async Task<string> Tree(
        [Description("目录路径")] string path = ".",
        [Description("最大深度 1-5")] int depth = 2)
        => await _fs.DirectoryTree(path, depth).ConfigureAwait(false);

    private string GetRelativePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(_ws, path));
            return Path.GetRelativePath(_ws, full);
        }
        catch
        {
            return path;
        }
    }

    private static string FormatSearchResults(string raw)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<search-results>");
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            sb.AppendLine($"  <match>{trimmed}</match>");
        }
        sb.Append("</search-results>");
        return sb.ToString();
    }
}
