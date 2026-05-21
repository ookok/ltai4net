namespace LTAI.Core.System;

public static class DomainKeywords
{
    private static readonly Dictionary<string, string[]> _keywords = new()
    {
        ["code"] = new[] { "code", "function", "class", "method", "编程", "代码", "函数", "bug", "debug", "api", "algorithm" },
        ["math"] = new[] { "calculate", "equation", "formula", "math", "计算", "公式", "数学", "求解", "积分", "导数" },
        ["science"] = new[] { "physics", "chemistry", "biology", "物理", "化学", "生物", "科学", "实验", "理论" },
        ["language"] = new[] { "translate", "grammar", "word", "翻译", "语法", "语言", "拼写", "词义", "sentence" },
        ["system"] = new[] { "system", "config", "setup", "install", "系统", "配置", "安装", "error", "log", "service" },
        ["creative"] = new[] { "write", "story", "poem", "creative", "写", "故事", "诗", "创意", "想象" },
        ["greeting"] = new[] { "hello", "hi", "你好", "早上好", "晚上好", "hey", "greetings" },
    };

    public static IReadOnlyDictionary<string, string[]> All => _keywords;

    public static string[] GetKeywords(string domain) =>
        _keywords.GetValueOrDefault(domain.ToLowerInvariant(), Array.Empty<string>());

    public static string InferDomain(string query)
    {
        var lower = query.ToLowerInvariant();
        var bestDomain = "general";
        var bestScore = 0;

        foreach (var (domain, keywords) in _keywords)
        {
            var score = keywords.Count(kw => lower.Contains(kw));
            if (score > bestScore)
            {
                bestScore = score;
                bestDomain = domain;
            }
        }

        return bestDomain;
    }
}
