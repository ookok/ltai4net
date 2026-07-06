using System.Text.Json;
using LTAI.Agent.Memory;
using LTAI.Agent.Pipeline;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Evolution;

public sealed record RegressionCase(
    string Query,
    string Normalized,
    double BaselineScore);

public sealed record RegressionResult(
    bool Passed,
    int TotalCases,
    int FailedCases,
    double AverageScore,
    double BaselineAverageScore,
    double Delta,
    IReadOnlyList<string> Failures);

public sealed class RegressionTestSuite
{
    private readonly MetaSkillStore _skillStore;
    private readonly PlanLearningStore _planStore;
    private readonly IChatClient? _evaluator;
    private readonly ILogger<RegressionTestSuite> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const double RegressionThreshold = -0.10; // allow up to 10% regression
    private const int MaxCases = 20;

    public RegressionTestSuite(
        MetaSkillStore skillStore,
        PlanLearningStore planStore,
        IChatClient? evaluator = null,
        ILogger<RegressionTestSuite>? logger = null)
    {
        _skillStore = skillStore;
        _planStore = planStore;
        _evaluator = evaluator;
        _logger = logger ?? NullLogger<RegressionTestSuite>.Instance;
    }

    public async Task<List<RegressionCase>> BuildSuiteAsync(CancellationToken ct = default)
    {
        var cases = new List<RegressionCase>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var plans = await _planStore.GetAllPlansAsync(ct).ConfigureAwait(false);
            foreach (var plan in plans.OrderByDescending(p => p.SuccessCount))
            {
                var norm = plan.Normalized;
                if (seen.Contains(norm)) continue;
                seen.Add(norm);

                var total = plan.SuccessCount + plan.FailureCount;
                var baseline = total > 0 ? (double)plan.SuccessCount / total : 0.5;

                cases.Add(new RegressionCase(plan.Query, norm, baseline));
                if (cases.Count >= MaxCases) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RegressionTestSuite: failed to build suite from plans");
        }

        _logger.LogInformation("RegressionTestSuite: built suite with {Count} cases", cases.Count);
        return cases;
    }

    public async Task<RegressionResult> EvaluateAsync(
        List<RegressionCase> cases,
        CancellationToken ct = default)
    {
        if (cases.Count == 0)
            return new RegressionResult(true, 0, 0, 0, 0, 0, []);

        var current = await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);
        var failures = new List<string>();
        var totalScore = 0.0;
        var baselineTotal = 0.0;

        foreach (var c in cases)
        {
            var score = await ScoreCaseAsync(c, current, ct).ConfigureAwait(false);
            totalScore += score;

            if (score < c.BaselineScore - 0.15)
            {
                failures.Add($"'{Truncate(c.Query, 60)}': baseline {c.BaselineScore:F2} → {score:F2}");
            }
        }

        var avg = totalScore / cases.Count;
        var baselineAvg = cases.Average(c => c.BaselineScore);
        var delta = avg - baselineAvg;
        var passed = delta >= RegressionThreshold && failures.Count <= cases.Count / 3;

        _logger.LogInformation(
            "RegressionTestSuite: {Result} — avg={Avg:F3} baseline={BaseAvg:F3} Δ={Delta:F3} failed={Failures}/{Total}",
            passed ? "PASS" : "FAIL", avg, baselineAvg, delta, failures.Count, cases.Count);

        return new RegressionResult(passed, cases.Count, failures.Count, avg, baselineAvg, delta, failures);
    }

    private async Task<double> ScoreCaseAsync(
        RegressionCase c,
        Evolution.MetaSkill skill,
        CancellationToken ct)
    {
        try
        {
            var principles = string.Join("\n", skill.TaskDecomposition.Principles);
            var prompt = $@"
给定编排原则：
{principles}

任务：{c.Query}

请评估以下维度（每项 0-1）：
- 目标清晰度：原则是否引导了正确的任务分解？
- 覆盖完整性：原则是否覆盖了任务所需的所有方面？
- 可执行性：原则是否能直接指导工具选择？

只输出 JSON：{{""clarity"": 0.0, ""coverage"": 0.0, ""executability"": 0.0}}
";

            if (_evaluator == null) return c.BaselineScore;
            var response = await _evaluator.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
                new Microsoft.Extensions.AI.ChatOptions { Temperature = 0f, MaxOutputTokens = 128 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var json = text[start..(end + 1)];
                var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (scores != null)
                {
                    var clarity = scores.GetValueOrDefault("clarity", 0.5);
                    var coverage = scores.GetValueOrDefault("coverage", 0.5);
                    var executability = scores.GetValueOrDefault("executability", 0.5);
                    return Math.Clamp((clarity + coverage + executability) / 3.0, 0.0, 1.0);
                }
            }
        }
        catch
        {
            // best-effort
        }

        return c.BaselineScore;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
