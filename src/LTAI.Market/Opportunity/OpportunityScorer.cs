using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Market.Opportunity;

public sealed class OpportunityScorer
{
    private static readonly Lazy<OpportunityScorer> _instance = new(() => new OpportunityScorer());
    public static OpportunityScorer Instance => _instance.Value;

    private readonly ILogger<OpportunityScorer> _logger;

    private static readonly Dictionary<string, float> StageUrgency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["招标公告"] = 1.0f,
        ["中标公告"] = 0.3f,
        ["审批批复"] = 0.7f,
        ["受理公示"] = 0.6f,
        ["环评公示"] = 0.8f,
        ["验收公示"] = 0.4f,
        ["更正公告"] = 0.5f,
        ["废标公告"] = 0.2f
    };

    private static readonly Dictionary<string, float> ProjectTypeBonus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["epc"] = 0.3f,
        ["总承包"] = 0.3f,
        ["运维"] = 0.2f,
        ["运营"] = 0.2f,
        ["设备采购"] = 0.15f,
        ["咨询服务"] = 0.1f,
        ["设计"] = 0.1f,
        ["检测"] = 0.15f,
        ["监测"] = 0.1f,
        ["治理"] = 0.2f,
        ["修复"] = 0.25f
    };

    private static readonly string[] CompetitionKeywords =
    {
        "多家", "竞争性", "磋商", "谈判", "公开招标", "比选", "入围", "资格预审"
    };

    private static readonly Regex ValueRegex = new(
        @"(\d+(?:\.\d+)?)\s*亿|\d+(?:\.\d+)?\s*万|\d+(?:\.\d+)?\s*万元",
        RegexOptions.Compiled);

    public OpportunityScorer(ILogger<OpportunityScorer>? logger = null)
    {
        _logger = logger ?? NullLogger<OpportunityScorer>.Instance;
    }

    public List<ScoredOpportunity> Score(UserProfile profile, List<Dictionary<string, object?>> announcements)
    {
        var results = new List<ScoredOpportunity>();

        foreach (var ann in announcements)
        {
            var title = GetString(ann, "title", "");
            var stage = GetString(ann, "stage", "");
            var date = GetString(ann, "date", DateTime.Now.ToString("yyyy-MM-dd"));
            var sourceUrl = GetString(ann, "source_url", null);

            var matchScore = CalcMatch(profile, title);
            var urgencyScore = CalcUrgency(stage, date);
            var profitScore = CalcProfit(title, profile);
            var competitionScore = CalcCompetition(title);
            var compositeScore = matchScore * 30f + urgencyScore * 25f + profitScore * 25f + (1f - competitionScore) * 20f;

            if (compositeScore < 20f)
                continue;

            var estimatedValue = EstimateValue(title, profile);
            var estimatedProfit = estimatedValue * 0.15f;
            var recommendedPrice = RecommendPrice(title, profile, competitionScore, estimatedValue);

            var recommendation = compositeScore switch
            {
                >= 70f => "立即跟进",
                >= 50f => "重点关注",
                >= 30f => "保持观望",
                _ => "暂不建议"
            };

            var opp = new ScoredOpportunity(
                ProjectName: title,
                Stage: stage,
                Date: date,
                CompositeScore: (float)Math.Round(compositeScore, 1),
                MatchScore: (float)Math.Round(matchScore, 2),
                UrgencyScore: (float)Math.Round(urgencyScore, 2),
                ProfitScore: (float)Math.Round(profitScore, 2),
                CompetitionScore: (float)Math.Round(competitionScore, 2),
                EstimatedValue: (float)Math.Round(estimatedValue, 1),
                EstimatedProfit: (float)Math.Round(estimatedProfit, 1),
                RecommendedPrice: recommendedPrice,
                CompetitorCount: 0,
                TopCompetitor: null,
                Recommendation: recommendation,
                SourceUrl: string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl
            );

            results.Add(opp);
        }

        return results.OrderByDescending(o => o.CompositeScore).ToList();
    }

    public float CalcMatch(UserProfile profile, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || profile.ServiceDomains.Length == 0)
            return 0.5f;

        var score = 0f;
        foreach (var domain in profile.ServiceDomains)
        {
            if (title.Contains(domain, StringComparison.OrdinalIgnoreCase))
                score += 0.3f;
        }
        return Math.Min(1.0f, score);
    }

    public float CalcUrgency(string stage, string date)
    {
        var score = 0.5f;

        if (!string.IsNullOrWhiteSpace(stage) && StageUrgency.TryGetValue(stage, out var baseScore))
            score = baseScore;

        if (DateTime.TryParse(date, out var parsedDate))
        {
            var daysDiff = (DateTime.Now - parsedDate).Days;
            if (daysDiff <= 7)
                score = Math.Min(1.0f, score + 0.2f);
            else if (daysDiff <= 30)
                score = Math.Min(1.0f, score + 0.1f);
        }

        return score;
    }

    public float CalcProfit(string title, UserProfile profile)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0.5f;

        var score = 0.5f;
        var lower = title.ToLowerInvariant();

        foreach (var (keyword, bonus) in ProjectTypeBonus)
        {
            if (lower.Contains(keyword))
                score += bonus;
        }

        return Math.Min(1.0f, score);
    }

    public float CalcCompetition(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0.5f;

        var count = CompetitionKeywords.Count(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
        return Math.Min(1.0f, count * 0.25f);
    }

    public float EstimateValue(string title, UserProfile profile)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 50f;

        var matches = ValueRegex.Matches(title);
        if (matches.Count > 0)
        {
            var match = matches[0].Value;
            if (match.Contains("亿"))
            {
                var numStr = match.Replace("亿", "").Trim();
                if (float.TryParse(numStr, out var num))
                    return num * 10000f;
                return 10000f;
            }
            else if (match.Contains("万"))
            {
                var numStr = match.Replace("万", "").Replace("元", "").Trim();
                if (float.TryParse(numStr, out var num))
                    return Math.Clamp(num, 0.05f, 500f);
                return 100f;
            }
        }

        var lower = title.ToLowerInvariant();
        if (lower.Contains("epc") || lower.Contains("总包") || lower.Contains("总承包"))
            return 500f;
        if (lower.Contains("运维") || lower.Contains("运营"))
            return 200f;
        if (lower.Contains("设备"))
            return 150f;
        if (lower.Contains("咨询"))
            return 80f;

        return profile.PriceRange.Max > 0 ? profile.PriceRange.Max : 100f;
    }

    public static string RecommendPrice(string title, UserProfile profile, float competitionScore, float estimatedValue)
    {
        var basePrice = estimatedValue;

        if (competitionScore > 0.5f)
            basePrice *= 0.85f + (1f - competitionScore) * 0.1f;
        else if (competitionScore < 0.3f)
            basePrice *= 1.05f + (0.3f - competitionScore) * 0.33f;

        return $"{basePrice:F0}万";
    }

    private static string GetString(Dictionary<string, object?> data, string key, string? defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? (defaultValue ?? "");
        return defaultValue ?? "";
    }
}

public sealed class MarketTrendAnalyzer
{
    private static readonly Lazy<MarketTrendAnalyzer> _instance = new(() => new MarketTrendAnalyzer());
    public static MarketTrendAnalyzer Instance => _instance.Value;

    private readonly ILogger<MarketTrendAnalyzer> _logger;

    public MarketTrendAnalyzer(ILogger<MarketTrendAnalyzer>? logger = null)
    {
        _logger = logger ?? NullLogger<MarketTrendAnalyzer>.Instance;
    }

    public Dictionary<string, object> Analyze(List<Dictionary<string, object?>> announcements)
    {
        var domainCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stageCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var monthlyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalEstimatedValue = 0f;
        var annCount = announcements.Count;

        foreach (var ann in announcements)
        {
            var domain = GetString(ann, "domain", "其他");
            var stage = GetString(ann, "stage", "未知");
            var date = GetString(ann, "date", "");

            if (!string.IsNullOrWhiteSpace(domain))
            {
                domainCount.TryGetValue(domain, out var dc);
                domainCount[domain] = dc + 1;
            }

            if (!string.IsNullOrWhiteSpace(stage))
            {
                stageCount.TryGetValue(stage, out var sc);
                stageCount[stage] = sc + 1;
            }

            if (date.Length >= 7)
            {
                var month = date[..7];
                monthlyCount.TryGetValue(month, out var mc);
                monthlyCount[month] = mc + 1;
            }

            var title = GetString(ann, "title", "");
            if (title.Contains("亿"))
                totalEstimatedValue += 5000f;
            else if (title.Contains("万"))
                totalEstimatedValue += 100f;
        }

        var trend = monthlyCount.OrderBy(kv => kv.Key).ToList();
        var growing = trend.Count >= 2 && trend[^1].Value > trend[^2].Value;

        return new Dictionary<string, object>
        {
            ["total_announcements"] = annCount,
            ["total_estimated_value_wan"] = totalEstimatedValue,
            ["domain_distribution"] = domainCount.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["stage_breakdown"] = stageCount.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["monthly_trend"] = trend.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["demand_trend"] = growing ? "上升" : "平稳",
            ["supply_competition"] = annCount > 20 ? "激烈" : "一般"
        };
    }

    private static string GetString(Dictionary<string, object?> data, string key, string defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? defaultValue;
        return defaultValue;
    }
}

public sealed class BiddingAssistant
{
    private static readonly Lazy<BiddingAssistant> _instance = new(() => new BiddingAssistant());
    public static BiddingAssistant Instance => _instance.Value;

    public string GenerateBidStrategy(UserProfile profile, ScoredOpportunity opportunity,
        List<Competitor> competitors)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 投标策略方案");
        sb.AppendLine();
        sb.AppendLine($"## 项目信息");
        sb.AppendLine($"- 项目名称: {opportunity.ProjectName}");
        sb.AppendLine($"- 项目阶段: {opportunity.Stage}");
        sb.AppendLine($"- 综合评分: {opportunity.CompositeScore:F1}/100");
        sb.AppendLine($"- 估算金额: {opportunity.EstimatedValue:F0}万");
        sb.AppendLine();
        sb.AppendLine("## 我方优势");
        sb.AppendLine($"- 企业资质: {profile.QualificationLevel}");
        sb.AppendLine($"- 服务领域: {string.Join(", ", profile.ServiceDomains)}");
        sb.AppendLine($"- 历史中标率: {(profile.ProjectsWon + profile.ProjectsLost > 0 ? (float)profile.ProjectsWon / (profile.ProjectsWon + profile.ProjectsLost) * 100 : 0):F0}%");
        sb.AppendLine();
        sb.AppendLine("## 竞争分析");
        if (competitors.Count > 0)
        {
            foreach (var c in competitors.OrderByDescending(c => c.WinRate).Take(3))
            {
                sb.AppendLine($"- {c.Name}: 中标率 {c.WinRate:P1}, 威胁等级 {c.ThreatLevel}");
            }
        }
        else
        {
            sb.AppendLine("- 暂无已知竞争对手");
        }
        sb.AppendLine();
        sb.AppendLine("## 定价建议");
        sb.AppendLine($"- 建议报价: **{opportunity.RecommendedPrice}**");
        sb.AppendLine($"- 预估利润: {opportunity.EstimatedProfit:F0}万");

        if (opportunity.CompetitionScore > 0.5f)
            sb.AppendLine("- 策略: 采取低价策略，突出我方技术和服务优势");
        else
            sb.AppendLine("- 策略: 合理定价，强调资质和经验优势");

        sb.AppendLine();
        sb.AppendLine("## 关键举措");
        sb.AppendLine("1. 准备资质文件和技术方案");
        sb.AppendLine("2. 分析竞争对手历史报价");
        sb.AppendLine("3. 明确项目负责人和团队配置");
        sb.AppendLine("4. 制定项目进度计划和风险预案");

        return sb.ToString();
    }

    public string GenerateTechnicalProposalOutline(UserProfile profile, string projectTitle)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 技术方案大纲");
        sb.AppendLine();
        sb.AppendLine($"## 项目名称: {projectTitle}");
        sb.AppendLine();
        sb.AppendLine("## 1. 项目理解");
        sb.AppendLine("- 项目背景与需求分析");
        sb.AppendLine("- 建设目标与技术路线");
        sb.AppendLine();
        sb.AppendLine("## 2. 技术方案");
        sb.AppendLine("- 总体技术架构");
        sb.AppendLine("- 关键技术路线");
        sb.AppendLine("- 系统功能设计");
        sb.AppendLine();
        sb.AppendLine("## 3. 实施方案");
        sb.AppendLine("- 项目组织架构");
        sb.AppendLine("- 实施进度计划");
        sb.AppendLine("- 质量保证措施");
        sb.AppendLine();
        sb.AppendLine("## 4. 公司资质与业绩");
        sb.AppendLine($"- 企业资质: {profile.QualificationLevel}");
        sb.AppendLine($"- 服务经验: {profile.ProjectsWon}个中标项目");
        sb.AppendLine($"- 服务领域: {string.Join(", ", profile.ServiceDomains)}");
        sb.AppendLine();
        sb.AppendLine("## 5. 人员配置");
        sb.AppendLine("- 项目负责人及团队介绍");
        sb.AppendLine("- 技术支持团队");
        sb.AppendLine();
        sb.AppendLine("## 6. 售后服务");
        sb.AppendLine("- 运维保障方案");
        sb.AppendLine("- 应急响应机制");
        sb.AppendLine("- 培训计划");

        return sb.ToString();
    }
}
