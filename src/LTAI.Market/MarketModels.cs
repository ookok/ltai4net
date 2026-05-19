using System.Text.Json.Serialization;

namespace LTAI.Market;

public sealed record UserProfile(
    [property: JsonPropertyName("company_name")] string CompanyName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("annual_revenue")] string AnnualRevenue,
    [property: JsonPropertyName("employee_count")] int EmployeeCount,
    [property: JsonPropertyName("qualification_level")] string QualificationLevel,
    [property: JsonPropertyName("service_radius")] string ServiceRadius,
    [property: JsonPropertyName("established_year")] int EstablishedYear,
    [property: JsonPropertyName("service_domains")] string[] ServiceDomains,
    [property: JsonPropertyName("avg_bidding_price")] string AvgBiddingPrice,
    [property: JsonPropertyName("price_range")] (float Min, float Max) PriceRange,
    [property: JsonPropertyName("projects_won")] int ProjectsWon,
    [property: JsonPropertyName("projects_lost")] int ProjectsLost,
    [property: JsonPropertyName("total_revenue_generated")] float TotalRevenueGenerated,
    [property: JsonPropertyName("known_competitors")] string[] KnownCompetitors,
    [property: JsonPropertyName("idle_capacity")] int IdleCapacity,
    [property: JsonPropertyName("profile_confidence")] float ProfileConfidence,
    [property: JsonPropertyName("last_updated")] string LastUpdated,
    [property: JsonPropertyName("data_sources")] string[] DataSources
);

public sealed record Competitor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("domains")] string[] Domains,
    [property: JsonPropertyName("win_count")] int WinCount,
    [property: JsonPropertyName("total_bids")] int TotalBids,
    [property: JsonPropertyName("avg_price")] string AvgPrice,
    [property: JsonPropertyName("first_seen")] string FirstSeen,
    [property: JsonPropertyName("last_seen")] string LastSeen,
    [property: JsonPropertyName("threat_level")] string ThreatLevel
)
{
    [JsonIgnore]
    public float WinRate => WinCount / (float)Math.Max(TotalBids, 1);
}

public sealed record ScoredOpportunity(
    [property: JsonPropertyName("project_name")] string ProjectName,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("composite_score")] float CompositeScore,
    [property: JsonPropertyName("match_score")] float MatchScore,
    [property: JsonPropertyName("urgency_score")] float UrgencyScore,
    [property: JsonPropertyName("profit_score")] float ProfitScore,
    [property: JsonPropertyName("competition_score")] float CompetitionScore,
    [property: JsonPropertyName("estimated_value")] float EstimatedValue,
    [property: JsonPropertyName("estimated_profit")] float EstimatedProfit,
    [property: JsonPropertyName("recommended_price")] string RecommendedPrice,
    [property: JsonPropertyName("competitor_count")] int CompetitorCount,
    [property: JsonPropertyName("top_competitor")] string? TopCompetitor,
    [property: JsonPropertyName("recommendation")] string Recommendation,
    [property: JsonPropertyName("source_url")] string? SourceUrl
);

public sealed record RevenueItem(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("estimated_value")] float EstimatedValue,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("source")] string Source
);

public sealed record MonthlyReport(
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("total_value")] float TotalValue,
    [property: JsonPropertyName("total_cost")] float TotalCost,
    [property: JsonPropertyName("roi")] float Roi,
    [property: JsonPropertyName("by_category")] Dictionary<string, float> ByCategory,
    [property: JsonPropertyName("top_items")] List<RevenueItem> TopItems,
    [property: JsonPropertyName("system_actions")] int SystemActions,
    [property: JsonPropertyName("trend")] string Trend
);

public sealed record InvestmentOption(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("upgrade_module")] string UpgradeModule,
    [property: JsonPropertyName("dev_cost_hours")] float DevCostHours,
    [property: JsonPropertyName("monthly_api_cost_increase")] float MonthlyApiCostIncrease,
    [property: JsonPropertyName("expected_monthly_value")] float ExpectedMonthlyValue,
    [property: JsonPropertyName("expected_annual_value")] float ExpectedAnnualValue,
    [property: JsonPropertyName("roi")] float Roi,
    [property: JsonPropertyName("priority")] string Priority
);

public sealed record ListedCompany(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("short_name")] string ShortName,
    [property: JsonPropertyName("stock_code")] string StockCode,
    [property: JsonPropertyName("exchange")] string Exchange,
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("sub_industry")] string SubIndustry,
    [property: JsonPropertyName("market_cap_category")] string MarketCapCategory,
    [property: JsonPropertyName("keywords")] string[] Keywords
);

public sealed record EconomicSignal(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("company")] ListedCompany Company,
    [property: JsonPropertyName("ann_title")] string AnnTitle,
    [property: JsonPropertyName("ann_date")] string AnnDate,
    [property: JsonPropertyName("signal_type")] string SignalType,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("inference")] string Inference,
    [property: JsonPropertyName("estimated_impact")] string EstimatedImpact,
    [property: JsonPropertyName("time_decay_factor")] float TimeDecayFactor,
    [property: JsonPropertyName("source_url")] string? SourceUrl
);
