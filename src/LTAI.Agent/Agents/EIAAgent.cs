using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class EIAAgent : BaseAgent
{
    private static readonly string[] RequiredStandards =
    {
        "GB 3095-2012", "HJ 2.2-2018", "HJ 2.1-2016", "HJ 2.4-2021",
        "HJ 610-2016", "HJ 19-2022", "GB 3838-2002", "GB 3096-2008"
    };

    private static readonly Dictionary<string, string> ValidStandards = new()
    {
        ["GB 3095-2012"] = "环境空气质量标准",
        ["GB 3838-2002"] = "地表水环境质量标准",
        ["GB 3096-2008"] = "声环境质量标准",
        ["GB 36600-2018"] = "土壤环境质量 建设用地土壤污染风险管控标准",
        ["GB 15618-2018"] = "土壤环境质量 农用地土壤污染风险管控标准",
        ["GB 16297-1996"] = "大气污染物综合排放标准",
        ["GB 8978-1996"] = "污水综合排放标准",
        ["GB 12348-2008"] = "工业企业厂界环境噪声排放标准",
        ["GB 14554-1993"] = "恶臭污染物排放标准",
        ["HJ 2.1-2016"] = "环境影响评价技术导则 总纲",
        ["HJ 2.2-2018"] = "环境影响评价技术导则 大气环境",
        ["HJ 2.3-2018"] = "环境影响评价技术导则 地表水环境",
        ["HJ 2.4-2021"] = "环境影响评价技术导则 声环境",
        ["HJ 610-2016"] = "环境影响评价技术导则 地下水",
        ["HJ 964-2018"] = "环境影响评价技术导则 土壤环境",
        ["HJ 19-2022"] = "环境影响评价技术导则 生态影响",
        ["HJ 169-2018"] = "建设项目环境风险评价技术导则",
        ["HJ 298-2019"] = "危险废物鉴别标准",
    };

    private static readonly Dictionary<string, (double min, double max, string unit)> ParamRanges = new()
    {
        ["Q"] = (0.001, 1_000_000, "g/s"), ["u"] = (0.5, 50, "m/s"),
        ["x"] = (1, 100_000, "m"), ["He"] = (1, 500, "m"),
        ["stability"] = (0, 0, "A-F"), ["Ts"] = (200, 2000, "K"),
        ["Ta"] = (200, 350, "K"), ["Vs"] = (0.1, 100, "m/s"),
        ["D"] = (0.1, 50, "m")
    };

    private static readonly Regex StandardRefPattern = new(
        @"(GB|HJ)\s*\d{2,5}[-—]\d{4}", RegexOptions.Compiled);

    public EIAAgent(
        LTAIAgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger<EIAAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct)
    {
        var query = context.UserQuery;
        _logger.LogInformation("EIAAgent [{Name}]: Processing EIA request", Name);

        var preCheck = ValidateEiaParameters(query);
        if (preCheck.Count > 0)
        {
            var warnings = string.Join("\n", preCheck.Select(w => $"- ⚠️ {w}"));
            context.FullHistory.Insert(0, new ChatMessage(ChatRole.System,
                $"Pre-request parameter validation:\n{warnings}\nPlease verify or clarify these parameters."));
        }

        var enhancedQuery = $"""
            Environmental Impact Assessment task. Follow these requirements:
            1. Reference applicable Chinese environmental standards ({string.Join(", ", RequiredStandards.Take(4))})
            2. Specify model methodology (e.g., Gaussian Plume, Streeter-Phelps, A-weighting)
            3. Include parameter values and their sources
            4. Quantify uncertainty where applicable
            5. Do not fabricate regulation numbers or monitoring data

            Request: {query}
            """;

        var enhancedMessages = new List<ChatMessage>(context.FullHistory.Take(context.FullHistory.Count - 1))
        {
            new(ChatRole.User, enhancedQuery)
        };

        _logger.LogInformation("EIAAgent [{Name}]: Enhanced prompt with {WarnCount} pre-check warnings", Name, preCheck.Count);

        var response = await CallBrainAsync(enhancedMessages, ct: ct);
        var responseText = response.Text ?? "";
        var complianceResult = AuditEiaResponse(responseText);

        if (complianceResult.Count > 0)
        {
            var issues = string.Join("\n", complianceResult.Select(i => $"- ❌ {i}"));
            _logger.LogWarning("EIAAgent [{Name}]: Compliance audit found {Count} issues", Name, complianceResult.Count);
            responseText += $"\n\n---\n## Compliance Audit\n{issues}";
            response.Messages = new List<ChatMessage> { new(ChatRole.Assistant, responseText) };
        }

        _logger.LogInformation("EIAAgent [{Name}]: Assessment complete, compliance score={Score}", Name,
            complianceResult.Count == 0 ? "PASS" : $"{complianceResult.Count} issues");
        return response;
    }

    private static List<string> ValidateEiaParameters(string query)
    {
        var warnings = new List<string>();

        foreach (var (param, (min, max, unit)) in ParamRanges)
        {
            var pattern = $@"{param}\s*[=:]\s*([\d.]+)";
            var match = Regex.Match(query, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            if (double.TryParse(match.Groups[1].Value, out var value))
            {
                if (param == "stability") continue;
                if (value < min)
                    warnings.Add($"Parameter {param}={value} is below typical range [{min}-{max}] {unit}");
                else if (value > max)
                    warnings.Add($"Parameter {param}={value} exceeds typical range [{min}-{max}] {unit}");
            }
        }

        return warnings;
    }

    private static List<string> AuditEiaResponse(string response)
    {
        var issues = new List<string>();

        var hasStandardRef = RequiredStandards.Any(s => response.Contains(s));
        if (!hasStandardRef)
            issues.Add("Missing references to applicable Chinese environmental standards (GB/HJ)");

        var standardMatches = StandardRefPattern.Matches(response);
        foreach (Match match in standardMatches)
        {
            var normalized = Regex.Replace(match.Value, @"\s+", " ").Replace("—", "-");
            if (!ValidStandards.ContainsKey(normalized))
                issues.Add($"Standard reference '{normalized}' not found in valid standards database — verify accuracy");
        }

        if (!Regex.IsMatch(response, @"\d+\.\d+\s*(μg|mg|g)/m[³3]", RegexOptions.IgnoreCase))
            issues.Add("Concentration values should include units (μg/m³ or mg/m³)");

        var suspiciousPatterns = new[] { "虚构", "假设", "approximately estimated" };
        if (suspiciousPatterns.Any(p => response.Contains(p, StringComparison.OrdinalIgnoreCase)))
            issues.Add("Response contains speculative language — verify with actual monitoring data");

        return issues;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CallBrainStreamingAsync(messages, cancellationToken))
            yield return update;
    }
}
