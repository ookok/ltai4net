using LTAI.Core.Messaging;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Governors;

public sealed class ToolSelector
{
    private static readonly HashSet<string> AlwaysInclude = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "shell_exec", "filesystem_list", "filesystem_read",
        "datetime_now", "env_sysinfo", "git_log", "git_diff"
    };

    private static readonly Dictionary<string, string[]> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = new[] { "搜索", "search", "查找", "查询", "百度", "google" },
        ["file"] = new[] { "文件", "file", "目录", "directory", "列出", "list", "读取", "read", "cat", "ls", "dir" },
        ["git"] = new[] { "git", "提交", "commit", "diff", "变更", "改动", "log", "branch", "分支" },
        ["shell"] = new[] { "执行", "运行", "run", "命令", "command", "cmd", "shell", "bash", "ps", "进程" },
        ["web"] = new[] { "网页", "url", "http", "抓取", "fetch", "下载", "download", "api" },
        ["math"] = new[] { "计算", "数学", "math", "公式", "转换", "convert", "进制" },
        ["datetime"] = new[] { "时间", "日期", "date", "time", "几点", "星期", "今天", "现在" },
        ["code"] = new[] { "代码", "code", "编程", "类", "class", "json", "csv", "格式化" },
        ["env"] = new[] { "系统", "环境", "system", "env", "内存", "cpu", "os", "操作系统" },
    };

    private readonly Dictionary<string, ToolMeta> _toolMeta;

    private sealed record ToolMeta(string Name, string Description, List<string> Keywords);

    public ToolSelector(AIToolRegistry toolRegistry)
    {
        _toolMeta = new Dictionary<string, ToolMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in toolRegistry.GetTools())
        {
            var name = tool.Name;
            var desc = tool.Description ?? "";
            var keywords = new List<string> { name };

            foreach (var (category, terms) in KeywordMap)
            {
                if (name.StartsWith(category, StringComparison.OrdinalIgnoreCase)
                    || desc.Contains(category, StringComparison.OrdinalIgnoreCase))
                {
                    keywords.AddRange(terms);
                }
            }

            _toolMeta[name] = new ToolMeta(name, desc, keywords);
        }
    }

    public List<AITool> SelectTools(string query, IEnumerable<AITool> allTools, int maxTools = 15)
    {
        var queryLower = query.ToLowerInvariant();
        var scored = new List<(AITool Tool, int Score)>();

        foreach (var tool in allTools)
        {
            if (!_toolMeta.TryGetValue(tool.Name, out var meta))
            {
                scored.Add((tool, 0));
                continue;
            }

            if (AlwaysInclude.Contains(tool.Name))
            {
                scored.Add((tool, int.MaxValue));
                continue;
            }

            var score = 0;
            foreach (var keyword in meta.Keywords)
            {
                if (queryLower.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score += keyword.Length;
            }

            var descWords = meta.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in descWords)
            {
                if (word.Length > 2 && queryLower.Contains(word, StringComparison.OrdinalIgnoreCase))
                    score += 1;
            }

            scored.Add((tool, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(maxTools)
            .Select(x => x.Tool)
            .ToList();
    }

    public IEnumerable<AITool> GetAlwaysInclude(IEnumerable<AITool> allTools)
    {
        return allTools.Where(t => AlwaysInclude.Contains(t.Name));
    }
}
