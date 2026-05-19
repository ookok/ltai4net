using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Market.Profiling;

public sealed class UserProfileEngine
{
    private static readonly Lazy<UserProfileEngine> _instance = new(() => new UserProfileEngine());
    public static UserProfileEngine Instance => _instance.Value;

    private readonly ILogger<UserProfileEngine> _logger;
    private readonly ConcurrentDictionary<string, UserProfile> _profiles = new();
    private readonly ConcurrentDictionary<string, Competitor> _competitors = new();

    private static readonly Regex CompanyNameRegex = new(
        @"[\u4e00-\u9fa5]{2,30}(?:有限(?:责任)?公司|集团(?:有限公司)?|(?:股份)?有限公司|厂|中心|研究院|设计院|事务所)",
        RegexOptions.Compiled);

    public UserProfileEngine(ILogger<UserProfileEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<UserProfileEngine>.Instance;
    }

    public UserProfile Build(string role, Dictionary<string, object?> collectedData)
    {
        if (_profiles.TryGetValue(role, out var existing))
            return existing;

        var companyName = GetString(collectedData, "company_name", role);
        var annualRevenue = GetString(collectedData, "annual_revenue", "500-1000万");
        var employeeCount = GetInt(collectedData, "employee_count", 50);
        var qualificationLevel = GetString(collectedData, "qualification_level", "乙级");
        var serviceRadius = GetString(collectedData, "service_radius", "全国");
        var establishedYear = GetInt(collectedData, "established_year", DateTime.Now.Year - 10);
        var avgBiddingPrice = GetString(collectedData, "avg_bidding_price", "50-200万");
        var priceMin = GetFloat(collectedData, "price_min", 50f);
        var priceMax = GetFloat(collectedData, "price_max", 200f);
        var projectsWon = GetInt(collectedData, "projects_won", 0);
        var projectsLost = GetInt(collectedData, "projects_lost", 0);
        var totalRevenue = GetFloat(collectedData, "total_revenue", 0f);
        var idleCapacity = GetInt(collectedData, "idle_capacity", 0);

        var domainsRaw = GetString(collectedData, "service_domains", "");
        var serviceDomains = string.IsNullOrWhiteSpace(domainsRaw)
            ? Array.Empty<string>()
            : domainsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var competitorsRaw = GetString(collectedData, "known_competitors", "");
        var knownCompetitors = string.IsNullOrWhiteSpace(competitorsRaw)
            ? Array.Empty<string>()
            : competitorsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sourcesRaw = GetString(collectedData, "data_sources", "manual");
        var dataSources = sourcesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var profile = new UserProfile(
            CompanyName: companyName,
            Role: role,
            AnnualRevenue: annualRevenue,
            EmployeeCount: employeeCount,
            QualificationLevel: qualificationLevel,
            ServiceRadius: serviceRadius,
            EstablishedYear: establishedYear,
            ServiceDomains: serviceDomains,
            AvgBiddingPrice: avgBiddingPrice,
            PriceRange: (priceMin, priceMax),
            ProjectsWon: projectsWon,
            ProjectsLost: projectsLost,
            TotalRevenueGenerated: totalRevenue,
            KnownCompetitors: knownCompetitors,
            IdleCapacity: idleCapacity,
            ProfileConfidence: 0.5f,
            LastUpdated: DateTime.Now.ToString("yyyy-MM-dd"),
            DataSources: dataSources
        );

        _profiles[role] = profile;
        _logger.LogInformation("Built profile for role: {Role}", role);
        return profile;
    }

    public void Update(UserProfile profile, Dictionary<string, object?> announcement)
    {
        var projectsWon = GetInt(announcement, "projects_won", 0);
        var projectsLost = GetInt(announcement, "projects_lost", 0);

        var updated = profile with
        {
            ProjectsWon = profile.ProjectsWon + projectsWon,
            ProjectsLost = profile.ProjectsLost + projectsLost,
            LastUpdated = DateTime.Now.ToString("yyyy-MM-dd"),
            ProfileConfidence = Math.Min(1.0f, profile.ProfileConfidence + 0.05f)
        };

        _profiles[profile.Role] = updated;
    }

    public List<Competitor> AnalyzeCompetitors(List<Dictionary<string, object?>> announcements)
    {
        var result = new List<Competitor>();
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        foreach (var ann in announcements)
        {
            var title = GetString(ann, "title", "");
            var names = ExtractCompanyNames(title);

            foreach (var name in names)
            {
                var domain = GetString(ann, "domain", "");
                var domains = string.IsNullOrWhiteSpace(domain)
                    ? Array.Empty<string>()
                    : new[] { domain };

                var competitor = _competitors.GetOrAdd(name, _ => new Competitor(
                    Name: name,
                    Domains: domains,
                    WinCount: 0,
                    TotalBids: 0,
                    AvgPrice: "",
                    FirstSeen: today,
                    LastSeen: today,
                    ThreatLevel: "低"
                ));

                var updated = competitor with
                {
                    TotalBids = competitor.TotalBids + 1,
                    LastSeen = today,
                    Domains = competitor.Domains.Length == 0 ? domains :
                        competitor.Domains.Union(domains).ToArray(),
                    ThreatLevel = ComputeThreatLevel(competitor.WinCount, competitor.TotalBids + 1)
                };

                _competitors[name] = updated;
                result.Add(updated);
            }
        }

        return result;
    }

    public string GetCompetitorReport(UserProfile profile)
    {
        var competitors = _competitors.Values
            .Where(c => profile.KnownCompetitors.Contains(c.Name) ||
                        c.Domains.Any(d => profile.ServiceDomains.Contains(d)))
            .OrderByDescending(c => c.WinRate)
            .ToList();

        if (competitors.Count == 0)
            return "## 竞争对手分析\n\n暂无已知竞争对手数据。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 竞争对手分析报告");
        sb.AppendLine();
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var c in competitors)
        {
            var icon = c.ThreatLevel switch
            {
                "高" => "🔴",
                "中" => "🟡",
                _ => "🟢"
            };
            sb.AppendLine($"### {icon} {c.Name}");
            sb.AppendLine($"- 威胁等级: **{c.ThreatLevel}**");
            sb.AppendLine($"- 中标率: {c.WinRate:P1} ({c.WinCount}/{c.TotalBids})");
            sb.AppendLine($"- 服务领域: {string.Join(", ", c.Domains)}");
            sb.AppendLine($"- 首次发现: {c.FirstSeen}");
            sb.AppendLine($"- 最近活动: {c.LastSeen}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GetCapacityReport(UserProfile profile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 闲置产能分析");
        sb.AppendLine();
        sb.AppendLine($"- 公司: {profile.CompanyName}");
        sb.AppendLine($"- 闲置产能人员: {profile.IdleCapacity}人");
        sb.AppendLine($"- 年营收: {profile.AnnualRevenue}");
        sb.AppendLine($"- 历史中标数: {profile.ProjectsWon}");
        sb.AppendLine($"- 历史流标数: {profile.ProjectsLost}");

        if (profile.ProjectsWon + profile.ProjectsLost > 0)
        {
            var winRate = (float)profile.ProjectsWon / (profile.ProjectsWon + profile.ProjectsLost);
            sb.AppendLine($"- 中标率: {winRate:P1}");
        }

        if (profile.IdleCapacity > 10)
        {
            sb.AppendLine();
            sb.AppendLine("⚠️ 闲置产能较高，建议积极拓展新项目机会。");
        }
        else if (profile.IdleCapacity < 3)
        {
            sb.AppendLine();
            sb.AppendLine("✅ 产能利用率较高，建议选择性跟进高价值项目。");
        }

        return sb.ToString();
    }

    public async Task SaveAsync(UserProfile profile)
    {
        var dir = Path.Combine(Environment.CurrentDirectory, ".livingtree", "profiles");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{profile.Role}.json");
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("Saved profile to {Path}", filePath);
    }

    public async Task<UserProfile?> LoadAsync(string role)
    {
        var filePath = Path.Combine(Environment.CurrentDirectory, ".livingtree", "profiles", $"{role}.json");
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        var profile = JsonSerializer.Deserialize<UserProfile>(json);
        if (profile != null)
            _profiles[role] = profile;

        return profile;
    }

    public static string[] ExtractCompanyNames(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Array.Empty<string>();

        return CompanyNameRegex.Matches(title)
            .Select(m => m.Value)
            .Distinct()
            .ToArray();
    }

    private static string ComputeThreatLevel(int winCount, int totalBids)
    {
        var winRate = totalBids > 0 ? (float)winCount / totalBids : 0f;
        if (winRate > 0.5f || winCount >= 3)
            return "高";
        if (winRate > 0.2f)
            return "中";
        return "低";
    }

    private static string GetString(Dictionary<string, object?> data, string key, string defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? defaultValue;
        return defaultValue;
    }

    private static int GetInt(Dictionary<string, object?> data, string key, int defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
        {
            if (val is int i) return i;
            if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static float GetFloat(Dictionary<string, object?> data, string key, float defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
        {
            if (val is float f) return f;
            if (float.TryParse(val.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }
}
