using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class StreamingPostProcessor
{
    private readonly ILogger<StreamingPostProcessor> _logger;

    public StreamingPostProcessor(ILogger<StreamingPostProcessor> logger)
    {
        _logger = logger;
    }

    public sealed record StreamContext
    {
        public string Query { get; init; } = "";
        public string FinalResponse { get; init; } = "";
        public string? Layer1Context { get; init; }
        public string? Layer2Context { get; init; }
        public string? PatternToolName { get; init; }
        public string Model { get; init; } = "";
        public int TotalToolCalls { get; init; }
        public bool GroundingFailed { get; init; }
        public bool Layer1HighConfidence { get; init; }
        public bool PatternMatched { get; init; }
        public float MetaFamiliarity { get; init; }
        public int RetryLevel { get; init; }
        public float ErlSuccessRate { get; init; }
        public int ErlTotalTrials { get; init; }
        public float BavtBudgetRatio { get; init; }
        public int RequestCount { get; init; }
        public string Label { get; init; } = "";
        public DateTime LastDreamCycleTrigger { get; init; }
        public bool HasGpu { get; init; }
    }

    public string? GetExplainabilityTrace(StreamContext ctx)
    {
        if (ctx.FinalResponse.Length <= 10)
            return null;

        return $"\n\n---\n[决策: L0={ctx.Label}, L1={ctx.PatternMatched}, L2={ctx.Layer2Context != null}, " +
               $"Model={ctx.Model}, Tools={ctx.TotalToolCalls}, Grounding={!ctx.GroundingFailed}, " +
               $"Familiarity={ctx.MetaFamiliarity:F2}, Budget={ctx.BavtBudgetRatio:F2}, " +
               $"Time={DateTime.UtcNow:HH:mm:ss}]";
    }

    public string? GetConfidenceHint(StreamContext ctx)
    {
        if (!ctx.GroundingFailed && ctx.MetaFamiliarity > 0.5f && ctx.FinalResponse.Length > 100)
            return "\n\n> 置信度: 高 | 格式建议: 结构化表格";
        return null;
    }

    public string? GetPersonaStyle(StreamContext ctx)
    {
        if (ctx.RetryLevel >= 2)
            return "concise";

        return ctx.FinalResponse.Length < 150 ? "concise" :
               ctx.FinalResponse.Count(c => c == '\n') > 5 ? "detailed" : "balanced";
    }

    public void LogBackpressure(int pendingCount)
    {
        if (pendingCount > 10)
            _logger.LogInformation("Backpressure: queue depth {Depth}, reducing aggressiveness", pendingCount);
    }

    public void LogBudgetRecovery(float budgetRatio, int requestCount)
    {
        if (budgetRatio < 0.5f && requestCount > 10)
        {
            var eta = budgetRatio < 0.1f ? "critical" : budgetRatio < 0.3f ? "low" : "moderate";
            _logger.LogInformation("BudgetRecovery: ratio={Ratio:F2}, status={Eta}, recommended={Rec}",
                budgetRatio, eta, budgetRatio < 0.3f ? "skip_non_essential_ops" : "normal");
        }
    }

    public void CalibrateConfidence(StreamContext ctx, Action<string, float> reinforceDomain, Action<string, bool> recordOutcome)
    {
        if (ctx.ErlSuccessRate > 0 && ctx.ErlSuccessRate < 0.5f && ctx.PatternToolName != null)
            reinforceDomain(ctx.PatternToolName, -0.05f);
        else if (ctx.ErlSuccessRate > 0.7f && ctx.PatternToolName != null)
            reinforceDomain(ctx.PatternToolName, 0.02f);

        if (ctx.GroundingFailed)
        {
            recordOutcome(ctx.Query, false);
            _logger.LogWarning("MetaCognition: recorded grounding failure for query: {Query}", ctx.Query[..Math.Min(ctx.Query.Length, 60)]);
        }
        else if (ctx.Layer1HighConfidence)
        {
            recordOutcome(ctx.Query, true);
            if (ctx.PatternToolName != null)
                reinforceDomain(ctx.PatternToolName, 0.1f);
        }
        else
        {
            var hasFailure = ctx.FinalResponse.Contains("未找到相关信息")
                || ctx.FinalResponse.Contains("无法")
                || ctx.FinalResponse.Length <= 20;
            recordOutcome(ctx.Query, !hasFailure);
        }
    }

    public void LogMemoryPressure()
    {
        if (Environment.WorkingSet > 2L * 1024 * 1024 * 1024)
            _logger.LogDebug("ResourceGuard: high memory usage ({Mem}MB), considering degradation",
                Environment.WorkingSet / 1024 / 1024);
    }

    public static readonly TimeSpan DreamCycleMinInterval = TimeSpan.FromMinutes(10);
}
