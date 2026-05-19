using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Market.Revenue;

public sealed class RevenueEngine
{
    private static readonly Lazy<RevenueEngine> _instance = new(() => new RevenueEngine());
    public static RevenueEngine Instance => _instance.Value;

    private readonly ILogger<RevenueEngine> _logger;
    private readonly List<RevenueItem> _items = new();
    private readonly object _itemsLock = new();
    private float _totalCost;
    private int _systemActions;

    public static readonly Dictionary<string, float> VALUE_RULES = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opportunity_discovered"] = 10f,
        ["opportunity_won"] = 35f,
        ["reminder_saved_penalty"] = 20f,
        ["template_saved_time"] = 1.5f,
        ["compliance_check"] = 5f,
        ["search_time_saved"] = 0.5f
    };

    public static readonly Dictionary<string, float> COST_RULES = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api_call_1k"] = 0.001f,
        ["storage_gb_month"] = 0.5f,
        ["compute_hour"] = 2.0f
    };

    public float MonthlyCost { get; set; }

    public RevenueEngine(ILogger<RevenueEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<RevenueEngine>.Instance;
    }

    public void Record(string category, string description, float value = 0f, float confidence = 1.0f)
    {
        if (value == 0f && VALUE_RULES.TryGetValue(category, out var autoValue))
            value = autoValue;

        var item = new RevenueItem(
            Date: DateTime.Now.ToString("yyyy-MM-dd"),
            Category: category,
            Description: description,
            EstimatedValue: value,
            Confidence: confidence,
            Source: "system"
        );

        lock (_itemsLock)
        {
            _items.Add(item);
            Interlocked.Increment(ref _systemActions);
        }

        _logger.LogInformation("Revenue: +{Value}万 ({Category}) - {Description}", value, category, description);
    }

    public void RecordCost(int apiCalls = 0, float storageGb = 0f, float computeHours = 0f)
    {
        var cost = 0f;

        if (COST_RULES.TryGetValue("api_call_1k", out var apiRate))
            cost += (apiCalls / 1000f) * apiRate;

        if (COST_RULES.TryGetValue("storage_gb_month", out var storageRate))
            cost += storageGb * storageRate;

        if (COST_RULES.TryGetValue("compute_hour", out var computeRate))
            cost += computeHours * computeRate;

        Interlocked.Exchange(ref _totalCost, _totalCost + cost);
        MonthlyCost += cost;
    }

    public MonthlyReport MonthlyReport(string? month = null)
    {
        month ??= DateTime.Now.ToString("yyyy-MM");

        List<RevenueItem> monthItems;
        lock (_itemsLock)
        {
            monthItems = _items
                .Where(i => i.Date.StartsWith(month))
                .OrderByDescending(i => i.EstimatedValue)
                .ToList();
        }

        var totalValue = monthItems.Sum(i => i.EstimatedValue);
        var totalCost = _totalCost;
        var roi = totalCost > 0 ? totalValue / totalCost : 0f;

        var byCategory = monthItems
            .GroupBy(i => i.Category)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.EstimatedValue));

        var topItems = monthItems.Take(10).ToList();

        var trend = totalValue switch
        {
            > 100f => "强劲增长",
            > 30f => "稳步增长",
            > 0f => "正常运营",
            _ => "暂无收入"
        };

        return new MonthlyReport(
            Month: month,
            TotalValue: totalValue,
            TotalCost: totalCost,
            Roi: roi,
            ByCategory: byCategory,
            TopItems: topItems,
            SystemActions: _systemActions,
            Trend: trend
        );
    }

    public Dictionary<string, object> GetStats()
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        var report = MonthlyReport(month);

        return new Dictionary<string, object>
        {
            ["month"] = month,
            ["total_value_wan"] = report.TotalValue,
            ["total_cost_wan"] = report.TotalCost,
            ["roi"] = report.Roi,
            ["system_actions"] = report.SystemActions,
            ["total_items"] = 0,
            ["trend"] = report.Trend
        };
    }
}

public sealed class SelfInvestmentEngine
{
    private static readonly Lazy<SelfInvestmentEngine> _instance = new(() => new SelfInvestmentEngine());
    public static SelfInvestmentEngine Instance => _instance.Value;

    private readonly ILogger<SelfInvestmentEngine> _logger;
    private readonly List<InvestmentOption> _options;

    public SelfInvestmentEngine(ILogger<SelfInvestmentEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<SelfInvestmentEngine>.Instance;

        _options = new List<InvestmentOption>
        {
            new(
                Name: "智能拓展",
                Description: "自动搜索和追踪同行业新项目机会，提升投标发现率",
                UpgradeModule: "market.hunt_expansion",
                DevCostHours: 8f,
                MonthlyApiCostIncrease: 0.5f,
                ExpectedMonthlyValue: 30f,
                ExpectedAnnualValue: 360f,
                Roi: 500f,
                Priority: "高"
            ),
            new(
                Name: "竞争对手情报",
                Description: "自动收集和分析竞争对手中标信息与策略",
                UpgradeModule: "market.competitor_intel",
                DevCostHours: 6f,
                MonthlyApiCostIncrease: 0.3f,
                ExpectedMonthlyValue: 20f,
                ExpectedAnnualValue: 240f,
                Roi: 400f,
                Priority: "高"
            ),
            new(
                Name: "投标自动化",
                Description: "自动生成投标策略和技术方案，减少人工投入",
                UpgradeModule: "market.bid_automation",
                DevCostHours: 16f,
                MonthlyApiCostIncrease: 0.8f,
                ExpectedMonthlyValue: 50f,
                ExpectedAnnualValue: 600f,
                Roi: 500f,
                Priority: "高"
            ),
            new(
                Name: "自动巡查频率提升",
                Description: "提高巡检频率从每日到每小时，提升机会捕获率",
                UpgradeModule: "core.auto_patrol_frequency",
                DevCostHours: 4f,
                MonthlyApiCostIncrease: 0.2f,
                ExpectedMonthlyValue: 5f,
                ExpectedAnnualValue: 60f,
                Roi: 100f,
                Priority: "中"
            ),
            new(
                Name: "大模型网关级联",
                Description: "启用多模型级联路由，降低API成本并提高推理质量",
                UpgradeModule: "ai.llm_gateway_cascade",
                DevCostHours: 12f,
                MonthlyApiCostIncrease: -2.0f,
                ExpectedMonthlyValue: 8f,
                ExpectedAnnualValue: 96f,
                Roi: 500f,
                Priority: "高"
            )
        };
    }

    public List<InvestmentOption> EvaluateOptions()
    {
        return _options.OrderByDescending(o => o.Roi).ToList();
    }

    public string Recommend(int topN = 3)
    {
        var top = _options.OrderByDescending(o => o.Roi).Take(topN).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 自我投资建议");
        sb.AppendLine();
        sb.AppendLine("基于当前系统运行数据，推荐以下升级方向：");
        sb.AppendLine();

        for (int i = 0; i < top.Count; i++)
        {
            var opt = top[i];
            var icon = opt.Priority == "高" ? "⭐" : "📋";
            sb.AppendLine($"## {i + 1}. {icon} {opt.Name} (ROI: {opt.Roi:P0})");
            sb.AppendLine($"- 描述: {opt.Description}");
            sb.AppendLine($"- 升级模块: `{opt.UpgradeModule}`");
            sb.AppendLine($"- 开发投入: {opt.DevCostHours}小时");
            sb.AppendLine($"- 月度API成本变化: {opt.MonthlyApiCostIncrease:+#.##;-#.##;0}万");
            sb.AppendLine($"- 预期月价值: {opt.ExpectedMonthlyValue:F1}万");
            sb.AppendLine($"- 预期年价值: {opt.ExpectedAnnualValue:F1}万");
            sb.AppendLine($"- 优先级: **{opt.Priority}**");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("*建议优先实施高ROI项目，以快速提升系统整体价值。*");

        return sb.ToString();
    }
}
