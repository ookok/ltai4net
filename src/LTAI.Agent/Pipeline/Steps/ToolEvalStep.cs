using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed record ToolEvalResult
{
    public double PassRate { get; init; }
    public double ChainCompleteness { get; init; }
    public double ArgumentQuality { get; init; }
    public double Efficiency { get; init; }
    public double OverallScore { get; init; }
    public List<string> Issues { get; init; } = [];
    public bool Passed => OverallScore >= EnvironmentConfig.ToolEvalPassThreshold;
}

public sealed class ToolEvalStep : IPipelineStep
{
    private readonly ILogger<ToolEvalStep> _logger;

    public string Name => "ToolEval";

    public ToolEvalStep(ILogger<ToolEvalStep>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolEvalStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var calls = context.ToolCalls;
        if (calls.Count == 0)
            return Task.FromResult(context);

        var result = Evaluate(calls, context);
        context.Set("ToolEvalResult", result);

        if (!result.Passed)
        {
            context.QualityGateBlocked = true;
            var msg = "⚠️ 工具使用评估未通过 (得分 " + result.OverallScore.ToString("P1") + ")\n"
                      + "- Pass Rate: " + result.PassRate.ToString("P1") + "\n"
                      + "- Chain Completeness: " + result.ChainCompleteness.ToString("P1") + "\n"
                      + "- Argument Quality: " + result.ArgumentQuality.ToString("P1") + "\n"
                      + "- Efficiency: " + result.Efficiency.ToString("P1") + "\n"
                      + "问题:\n"
                      + string.Join("\n", result.Issues.Select(i => "- " + i));
            lock (context.MessagesLock)
                context.Messages.Add(new ChatMessage(ChatRole.System, msg));
        }

        _logger.LogDebug("ToolEval: score={Score:P1} on {Count} calls",
            result.OverallScore, calls.Count);

        return Task.FromResult(context);
    }

    private static ToolEvalResult Evaluate(
        List<(string Name, string Arguments, string Result)> calls,
        MessageContext context)
    {
        var issues = new List<string>();

        var totalCalls = calls.Count;
        if (totalCalls == 0)
            return new ToolEvalResult { OverallScore = 1.0, PassRate = 1.0, ChainCompleteness = 1.0, ArgumentQuality = 1.0, Efficiency = 1.0 };

        var successCount = calls.Count(c => !string.IsNullOrWhiteSpace(c.Result)
            && !c.Result.Contains("error", StringComparison.OrdinalIgnoreCase)
            && !c.Result.Contains("exception", StringComparison.OrdinalIgnoreCase));
        var passRate = (double)successCount / totalCalls;

        if (passRate < 0.7)
            issues.Add("工具调用成功率偏低 (" + successCount + "/" + totalCalls + ")");

        var nameGroups = calls.GroupBy(c => c.Name);
        var callChain = nameGroups.Count();
        var chainCompleteness = Math.Min(1.0, callChain / Math.Max(1.0, totalCalls * 0.5));
        if (chainCompleteness < 0.5)
            issues.Add("工具链不完整: 仅使用了 " + callChain + " 种不同类型工具");

        var argQuality = 1.0;
        foreach (var (name, args, _) in calls)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                argQuality -= 0.1;
                issues.Add("工具 '" + name + "' 缺少参数");
            }
            else if (args.Length < 5)
            {
                argQuality -= 0.05;
            }
        }
        argQuality = Math.Max(0, argQuality);

        var efficientCallCount = nameGroups
            .Where(g => g.Count() <= 2)
            .Sum(g => g.Count());
        var efficiency = totalCalls > 0 ? (double)efficientCallCount / totalCalls : 1.0;
        if (efficiency < 0.5)
            issues.Add("工具效率低: 存在过多重复调用");

        var weightedSum = passRate * 1.0 + chainCompleteness * 0.8 + argQuality * 0.6 + efficiency * 0.6;
        var overallScore = weightedSum / 3.0;

        return new ToolEvalResult
        {
            PassRate = passRate,
            ChainCompleteness = chainCompleteness,
            ArgumentQuality = argQuality,
            Efficiency = efficiency,
            OverallScore = overallScore,
            Issues = issues,
        };
    }
}
