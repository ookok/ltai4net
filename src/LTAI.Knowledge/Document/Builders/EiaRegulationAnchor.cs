using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Knowledge.Document;

public sealed class EiaRegulationAnchor
{
    private static readonly EiaRegulation[] Regulations =
    {
        new("GB 3095-2012", "环境空气质量标准", "大气",
            "规定各项污染物的浓度限值。适用于全国范围的环境空气质量评价与管理。",
            new[] { "SO2", "NO2", "PM10", "PM2.5", "CO", "O3" }, DateTime.Parse("2016-01-01")),

        new("HJ 2.2-2018", "环境影响评价技术导则 大气环境", "大气",
            "规定大气环境影响评价的方法、内容与要求。包括评价等级判定、污染源调查、浓度预测等。",
            new[] { "AERMOD", "CALPUFF", "ADMS" }, DateTime.Parse("2018-12-01")),

        new("HJ 2.1-2016", "建设项目环境影响评价技术导则 总纲", "通用",
            "规定建设项目环境影响评价的一般性原则、方法、内容及要求。",
            Array.Empty<string>(), DateTime.Parse("2017-01-01")),

        new("HJ 2.4-2021", "环境影响评价技术导则 声环境", "噪声",
            "规定声环境影响评价的方法、内容与要求，包括噪声预测模型与评价标准。",
            new[] { "CadnaA", "SoundPLAN" }, DateTime.Parse("2022-01-01")),

        new("HJ 610-2016", "环境影响评价技术导则 地下水环境", "水",
            "规定地下水环境影响评价的技术要求，包括水文地质调查、溶质运移预测等。",
            Array.Empty<string>(), DateTime.Parse("2016-07-01")),

        new("GB 3838-2002", "地表水环境质量标准", "水",
            "规定地表水环境质量分类及标准值。适用于江河、湖泊、水库等。",
            new[] { "DO", "COD", "BOD5", "NH3-N", "TP", "TN" }, DateTime.Parse("2002-06-01")),

        new("GB 3096-2008", "声环境质量标准", "噪声",
            "规定五类声环境功能区的环境噪声限值。",
            new[] { "Leq", "L10", "L50", "L90" }, DateTime.Parse("2008-10-01")),

        new("HJ 19-2022", "环境影响评价技术导则 生态影响", "生态",
            "规定生态影响评价的技术要求，包括生态系统调查、生物多样性评估等。",
            Array.Empty<string>(), DateTime.Parse("2022-07-01")),

        new("HJ 169-2018", "建设项目环境风险评价技术导则", "风险",
            "规定环境风险评价的程序、方法与内容。",
            Array.Empty<string>(), DateTime.Parse("2019-01-01")),

        new("GB 13271-2014", "锅炉大气污染物排放标准", "大气",
            "规定锅炉大气污染物排放限值。",
            new[] { "SO2", "NOx", "颗粒物" }, DateTime.Parse("2014-07-01")),

        new("GB 8978-1996", "污水综合排放标准", "水",
            "规定水污染物排放限值。",
            new[] { "COD", "BOD5", "SS", "NH3-N" }, DateTime.Parse("1998-01-01")),

        new("GB 12348-2008", "工业企业厂界环境噪声排放标准", "噪声",
            "规定工业企业厂界环境噪声排放限值。",
            new[] { "Leq" }, DateTime.Parse("2008-10-01")),
    };

    private static readonly string[] HallucinationPatterns =
    {
        @"GB\s+\d{4,5}-\d{4}(?!2012|2018|2016|2021|2002|2008|2022|2014|1996|1998)",
        @"HJ\s+\d{3,4}-\d{4}(?!2\.2-2018|2\.1-2016|2\.4-2021|610-2016|19-2022|169-2018)",
        @"DB\d{2}/\d+-\d{4}", @"\bISO\s+\d{4,6}[:\-]\d{4}\b",
        @"WS/?T\s*\d{3}-\d{4}", @"CJ/?T\s*\d{3}-\d{4}"
    };

    public static List<EiaRegulation> Search(string query)
    {
        var q = query.ToLowerInvariant();
        return Regulations
            .Where(r =>
                r.Id.ToLowerInvariant().Contains(q) ||
                r.Name.ToLowerInvariant().Contains(q) ||
                r.Category.ToLowerInvariant().Contains(q) ||
                r.Keywords.Any(k => k.ToLowerInvariant().Contains(q)))
            .ToList();
    }

    public static string GetVerificationPrompt()
    {
        var refs = Regulations.Select(r => $"- {r.Id} ({r.Name}, 生效 {r.EffectiveDate:yyyy-MM-dd})");
        return $"""
            You are generating an EIA (Environmental Impact Assessment) report.
            When citing Chinese environmental standards, ONLY use the following verified references.
            DO NOT fabricate or invent regulation numbers.

            Verified standards:
            {string.Join("\n", refs)}

            If a regulation is not listed above, do NOT reference it. Instead, note that the standard
            requires verification against the current regulatory database.
            """;
    }

    public static (bool valid, List<string> issues) ValidateRegulationReferences(string text)
    {
        var issues = new List<string>();

        foreach (var pattern in HallucinationPatterns)
        {
            var matches = Regex.Matches(text, pattern);
            foreach (Match match in matches)
            {
                var known = Regulations.Any(r => text.Contains(r.Id));
                if (!known)
                    issues.Add($"Potentially fabricated regulation: {match.Value} — not in verified database");
            }
        }

        if (text.Contains("approximately", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(text, @"\d+\.\d+\s*(μg|mg|g)/m[³3]"))
            issues.Add("Exact values should be reported — avoid approximate ranges for compliance data");

        return (issues.Count == 0, issues);
    }

    public static string BuildContextPrompt(string domain)
    {
        var relevant = Regulations.Where(r => r.Category == domain || domain == "通用").ToList();
        if (relevant.Count == 0) return "";

        var lines = relevant.Select(r =>
            $"- {r.Id}: {r.Name} (effective {r.EffectiveDate:yyyy-MM-dd}) — {r.Description}");
        return $"Applicable standards for {domain}:\n{string.Join("\n", lines)}";
    }
}

public sealed record EiaRegulation(
    string Id,
    string Name,
    string Category,
    string Description,
    string[] Keywords,
    DateTime EffectiveDate);
