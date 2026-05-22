using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace LTAI.Capability.Search;

public sealed class PlatformSearchEntry
{
    public string Name { get; init; } = "";
    public string SitePrefix { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Aliases { get; init; } = new();
    public bool RequiresLogin { get; init; }
    public string? SearchUrlHint { get; init; }
}

public sealed class PlatformSearchService
{
    private static readonly Lazy<PlatformSearchService> _instance = new(() => new PlatformSearchService());
    public static PlatformSearchService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, PlatformSearchEntry> _platforms = new();

    public IReadOnlyDictionary<string, PlatformSearchEntry> Platforms => _platforms;

    private PlatformSearchService()
    {
        SeedPlatforms();
    }

    private void SeedPlatforms()
    {
        var platforms = new List<PlatformSearchEntry>
        {
            new() { Name = "csdn", SitePrefix = "site:csdn.net", Category = "技术社区", Description = "CSDN 中文技术博客社区", Aliases = new() { "技术博客", "CSDN", "csdn.net" } },
            new() { Name = "zhihu", SitePrefix = "site:zhihu.com", Category = "问答社区", Description = "知乎高质量问答平台", Aliases = new() { "问答", "知乎", "zhihu" } },
            new() { Name = "toutiao", SitePrefix = "site:toutiao.com", Category = "新闻资讯", Description = "今日头条新闻/文章", Aliases = new() { "头条", "新闻", "toutiao" } },
            new() { Name = "wechat", SitePrefix = "site:mp.weixin.qq.com", Category = "社交平台", Description = "微信公众号文章", Aliases = new() { "微信", "公众号", "weixin" } },
            new() { Name = "xiaohongshu", SitePrefix = "site:xiaohongshu.com", Category = "生活社区", Description = "小红书笔记和攻略", Aliases = new() { "小红书", "redbook", "xhs" } },
            new() { Name = "juejin", SitePrefix = "site:juejin.cn", Category = "技术社区", Description = "掘金中文技术社区", Aliases = new() { "掘金", "juejin" } },
            new() { Name = "bilibili", SitePrefix = "site:bilibili.com", Category = "视频平台", Description = "B站视频/专栏文章", Aliases = new() { "B站", "b站", "哔哩哔哩" } },
            new() { Name = "weibo", SitePrefix = "site:weibo.com", Category = "社交平台", Description = "微博热搜/文章", Aliases = new() { "微博", "热搜" } },
            new() { Name = "segmentfault", SitePrefix = "site:segmentfault.com", Category = "技术社区", Description = "思否技术问答", Aliases = new() { "思否", "segmentfault" } },
            new() { Name = "zhuanlan", SitePrefix = "site:zhuanlan.zhihu.com", Category = "技术专栏", Description = "知乎专栏深度文章", Aliases = new() { "专栏", "知乎专栏" } },
            new() { Name = "v2ex", SitePrefix = "site:v2ex.com", Category = "技术社区", Description = "V2EX 创意工作者社区", Aliases = new() { "v2", "v2ex" } },
            new() { Name = "github_zh", SitePrefix = "site:github.com", Category = "开发者", Description = "GitHub 中文项目/README/Issue", Aliases = new() { "github中文" }, SearchUrlHint = "language:Chinese" },
            new() { Name = "wikipedia_zh", SitePrefix = "site:zh.wikipedia.org", Category = "知识百科", Description = "中文维基百科", Aliases = new() { "维基", "百科", "wiki" } },
            new() { Name = "baike", SitePrefix = "site:baike.baidu.com", Category = "知识百科", Description = "百度百科", Aliases = new() { "百度百科", "百科" } },
            new() { Name = "douban", SitePrefix = "site:douban.com", Category = "生活社区", Description = "豆瓣书评/影评/小组", Aliases = new() { "豆瓣", "书评", "影评" } },
            new() { Name = "36kr", SitePrefix = "site:36kr.com", Category = "科技资讯", Description = "36氪科技商业资讯", Aliases = new() { "36氪", "创投" } },
            new() { Name = "infoq", SitePrefix = "site:infoq.cn", Category = "技术社区", Description = "InfoQ 中国技术资讯", Aliases = new() { "InfoQ", "infoq" } },
            new() { Name = "oschina", SitePrefix = "site:oschina.net", Category = "技术社区", Description = "开源中国技术社区", Aliases = new() { "开源中国", "oschina", "gitee" } },
            new() { Name = "cnblogs", SitePrefix = "site:cnblogs.com", Category = "技术社区", Description = "博客园 .NET 技术博客", Aliases = new() { "博客园", "cnblogs" } },
            new() { Name = "jianshu", SitePrefix = "site:jianshu.com", Category = "写作平台", Description = "简书文章/教程", Aliases = new() { "简书" } },
            new() { Name = "gov_cn", SitePrefix = "site:gov.cn", Category = "政府网站", Description = "中国政府网站公文/政策/公告", Aliases = new() { "政府", "政策", "法规", "gov" } },
            new() { Name = "mee", SitePrefix = "site:mee.gov.cn", Category = "政府部门", Description = "生态环境部 环评/标准/法规", Aliases = new() { "生态环境部", "环保", "EIA", "环评" } },
            new() { Name = "ndrc", SitePrefix = "site:ndrc.gov.cn", Category = "政府部门", Description = "国家发改委 政策/规划", Aliases = new() { "发改委", "规划", "产业政策" } },
            new() { Name = "mohurd", SitePrefix = "site:mohurd.gov.cn", Category = "政府部门", Description = "住建部 建筑/工程标准", Aliases = new() { "住建部", "建筑", "工程" } },
        };

        foreach (var p in platforms)
            _platforms[p.Name] = p;
    }

    public string BuildSearchQuery(string query, string platformName)
    {
        if (!_platforms.TryGetValue(platformName, out var platform))
            return query;

        var siteQuery = platform.SitePrefix;
        if (platform.SearchUrlHint != null)
            siteQuery += " " + platform.SearchUrlHint;

        return $"{query} {siteQuery}";
    }

    public PlatformSearchEntry? Resolve(string nameOrAlias)
    {
        var lower = nameOrAlias.ToLower();
        if (_platforms.TryGetValue(lower, out var direct))
            return direct;

        foreach (var (_, p) in _platforms)
        {
            if (p.Aliases.Any(a => a.ToLower().Contains(lower) || lower.Contains(a.ToLower())))
                return p;
        }

        return null;
    }

    public List<PlatformSearchEntry> ListByCategory(string category)
    {
        return _platforms.Values
            .Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public List<string> ListCategories()
    {
        return _platforms.Values
            .Select(p => p.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }

    public string BuildPromptContext()
    {
        var cats = ListCategories();
        var lines = new List<string>
        {
            "## 内容平台搜索目录",
            "",
            $"共 {_platforms.Count} 个平台，覆盖 {cats.Count} 个类别。",
            "使用 platform_search 工具指定平台进行内容搜索。",
            ""
        };

        foreach (var cat in cats)
        {
            var platforms = ListByCategory(cat);
            lines.Add($"### {cat} ({platforms.Count})");
            foreach (var p in platforms.Take(15))
                lines.Add($"- **{p.Name}**: {p.Description} (别名: {string.Join(", ", p.Aliases.Take(3))})");
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["total_platforms"] = _platforms.Count,
            ["categories"] = ListCategories().Count,
            ["prompt_length"] = BuildPromptContext().Length
        };
    }
}
