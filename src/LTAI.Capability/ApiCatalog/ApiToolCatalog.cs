using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LTAI.Capability.ApiCatalog;

public sealed class ApiToolEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Free { get; set; } = true;
    public bool RequiresApiKey { get; set; }
    public string? ApiKeyProvider { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public Dictionary<string, object?>? Handler { get; set; }
    public AITool? AITool { get; set; }
}

public sealed class ApiToolCatalog
{
    private static readonly Lazy<ApiToolCatalog> _instance = new(() => new ApiToolCatalog());
    public static ApiToolCatalog Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ApiToolEntry> _tools = new();
    private bool _loaded;

    public IReadOnlyDictionary<string, ApiToolEntry> AllTools => _tools;

    private ApiToolCatalog()
    {
        SeedBuiltInApis();
        _loaded = true;
    }

    private void SeedBuiltInApis()
    {
        Register(new ApiToolEntry
        {
            Name = "api_weather",
            Description = "Get real-time weather for any city worldwide (OpenWeatherMap free tier: 60 calls/min). Parameters: city (城市名), lang (语言,默认zh_cn)",
            Category = "utility",
            Parameters = new() { ["city"] = "北京", ["lang"] = "zh_cn" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_air_quality",
            Description = "Get air quality index AQI for any city (免费, 需API Key). Parameters: city (城市名)",
            Category = "environment",
            RequiresApiKey = true,
            ApiKeyProvider = "OpenWeatherMap",
            Parameters = new() { ["city"] = "北京" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_translate",
            Description = "Translate text between languages via Baidu Translate API (free tier: 200万字符/月). Parameters: text, from_lang (默认auto), to_lang (默认zh)",
            Category = "text",
            RequiresApiKey = true,
            ApiKeyProvider = "Baidu",
            Parameters = new() { ["text"] = "Hello", ["from_lang"] = "auto", ["to_lang"] = "zh" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_ip_location",
            Description = "Lookup IP address geographic location (ip-api.com, free, no key required). Parameters: ip (IP地址)",
            Category = "network",
            Parameters = new() { ["ip"] = "8.8.8.8" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_geocode",
            Description = "Forward geocoding: convert address to lat/lng coordinates (高德地图API). Parameters: address (地址), city (可选城市过滤)",
            Category = "gis",
            RequiresApiKey = true,
            ApiKeyProvider = "Amap",
            Parameters = new() { ["address"] = "北京市朝阳区", ["city"] = "北京" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_reverse_geocode",
            Description = "Reverse geocoding: convert lat/lng to human-readable address (高德地图API). Parameters: lat, lng",
            Category = "gis",
            RequiresApiKey = true,
            ApiKeyProvider = "Amap",
            Parameters = new() { ["lat"] = "39.9", ["lng"] = "116.4" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_route",
            Description = "Calculate driving/walking/transit route between two points (高德地图API). Parameters: origin_lat, origin_lng, dest_lat, dest_lng, mode (driving/walking/transit)",
            Category = "gis",
            RequiresApiKey = true,
            ApiKeyProvider = "Amap",
            Parameters = new() { ["origin_lat"] = "39.9", ["origin_lng"] = "116.4", ["dest_lat"] = "39.9", ["dest_lng"] = "116.5", ["mode"] = "driving" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_poi_search",
            Description = "Search for points of interest nearby or in area (高德地图API). Parameters: keywords (关键词), city, types (类型代码)",
            Category = "gis",
            RequiresApiKey = true,
            ApiKeyProvider = "Amap",
            Parameters = new() { ["keywords"] = "酒店", ["city"] = "北京" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_sms_send",
            Description = "Send SMS message via Alibaba Cloud SMS (阿里云短信服务). Parameters: phone (手机号), template_code (模板), template_params (JSON模板参数)",
            Category = "communication",
            RequiresApiKey = true,
            ApiKeyProvider = "Aliyun",
            Parameters = new() { ["phone"] = "13800138000", ["template_code"] = "SMS_123456" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_image_search",
            Description = "Search for free stock photos (Unsplash API, 50 req/hour free). Parameters: query (关键词), per_page (最大30)",
            Category = "media",
            Parameters = new() { ["query"] = "landscape", ["per_page"] = "10" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_github_user",
            Description = "Lookup GitHub user profile and repos (免费, 无需API Key). Parameters: username (GitHub用户名)",
            Category = "dev",
            Parameters = new() { ["username"] = "torvalds" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_arxiv_search",
            Description = "Search academic papers on arXiv (免费, 无需API Key). Parameters: query (搜索词), max_results (最大20)",
            Category = "academic",
            Parameters = new() { ["query"] = "large language model", ["max_results"] = "5" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_timezone",
            Description = "Get current time and timezone info for any location (WorldTimeAPI, free). Parameters: location (地区名,如 Asia/Shanghai)",
            Category = "utility",
            Parameters = new() { ["location"] = "Asia/Shanghai" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_currency_convert",
            Description = "Convert currency amounts at real-time rates (免费API). Parameters: from (源币种), to (目标币种), amount (金额)",
            Category = "finance",
            Parameters = new() { ["from"] = "USD", ["to"] = "CNY", ["amount"] = "100" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_public_apis",
            Description = "Search the public-apis catalog (1400+ free public APIs across 50+ categories). Parameters: query (搜索词), category (分类名,可选)",
            Category = "discovery",
            Parameters = new() { ["query"] = "weather" }
        });

        Register(new ApiToolEntry
        {
            Name = "api_image_generate",
            Description = "Text-to-image generation (placeholder - requires external model). Parameters: prompt (描述词), style (风格), size (尺寸)",
            Category = "media",
            RequiresApiKey = true,
            Parameters = new() { ["prompt"] = "Generate a landscape image", ["style"] = "realistic" }
        });
    }

    public void Register(ApiToolEntry entry)
    {
        var aiFunc = AIFunctionFactory.Create(
            (string query) => Task.FromResult<object?>($"API call: {entry.Name} with query={query}"),
            entry.Name, entry.Description);

        entry.AITool = aiFunc;
        _tools[entry.Name] = entry;
    }

    public List<ApiToolEntry> Search(string query)
    {
        var q = query.ToLowerInvariant();
        return _tools.Values
            .Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       t.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       t.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Free ? 0 : 1)
            .Take(20)
            .ToList();
    }

    public List<ApiToolEntry> ListByCategory(string category)
    {
        return _tools.Values
            .Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public List<string> ListCategories()
    {
        return _tools.Values
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }

    public string BuildPromptContext()
    {
        var categories = ListCategories();
        var lines = new List<string>
        {
            "## 内置免费API工具目录",
            "",
            $"共 {_tools.Count} 个API工具，覆盖 {categories.Count} 个类别。",
            "调用方式: api_<name>(参数...)。免费API无需密钥直接调用。",
            ""
        };

        foreach (var category in categories)
        {
            var tools = ListByCategory(category);
            lines.Add($"### {GetCategoryEmoji(category)} {GetCategoryNameZh(category)} ({tools.Count})");
            foreach (var t in tools.Take(10))
            {
                var freeTag = t.Free ? "免费" : "需Key";
                var keyInfo = t.RequiresApiKey ? $" [需{t.ApiKeyProvider}密钥]" : "";
                var paramsInfo = string.Join(", ", t.Parameters.Keys);
                lines.Add($"- **{t.Name}** {freeTag}: {t.Description[..Math.Min(t.Description.Length, 120)]}{keyInfo}");
                if (t.Parameters.Count > 0)
                    lines.Add($"  参数: {paramsInfo}");
            }
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    private static string GetCategoryEmoji(string cat) => cat switch
    {
        "utility" => "🌤",
        "environment" => "🌿",
        "text" => "📝",
        "network" => "🌐",
        "gis" => "🗺",
        "communication" => "📱",
        "media" => "🎨",
        "dev" => "💻",
        "academic" => "📚",
        "finance" => "💰",
        "discovery" => "🔍",
        _ => "📦"
    };

    private static string GetCategoryNameZh(string cat) => cat switch
    {
        "utility" => "实用工具",
        "environment" => "环境监测",
        "text" => "文本处理",
        "network" => "网络工具",
        "gis" => "地理信息",
        "communication" => "通信服务",
        "media" => "媒体资源",
        "dev" => "开发者工具",
        "academic" => "学术资源",
        "finance" => "金融数据",
        "discovery" => "API发现",
        _ => cat
    };

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["total_apis"] = _tools.Count,
            ["free_apis"] = _tools.Values.Count(t => t.Free),
            ["keyed_apis"] = _tools.Values.Count(t => t.RequiresApiKey),
            ["categories"] = ListCategories().Count,
            ["prompt_length"] = BuildPromptContext().Length
        };
    }
}
