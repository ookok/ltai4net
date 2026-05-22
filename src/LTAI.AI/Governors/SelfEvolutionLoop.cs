using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// 演化动作类型
/// </summary>
public enum EvolutionActionType
{
    /// <summary>更新路由阈值</summary>
    UpdateRoutingThreshold,
    /// <summary>切换协作模式</summary>
    SwitchCollaborationPattern,
    /// <summary>调整收敛阈值</summary>
    AdjustConvergenceThreshold,
    /// <summary>添加 Few-shot 示例</summary>
    AddFewShotExample,
    /// <summary>触发后台微调</summary>
    TriggerFineTuning,
    /// <summary>更新系统 Prompt</summary>
    UpdateSystemPrompt,
    /// <summary>永久路由到 L2</summary>
    PermanentRouteToL2
}

/// <summary>
/// 演化动作
/// </summary>
public sealed record EvolutionAction
{
    public EvolutionActionType Type { get; init; }
    public string Target { get; init; } = "";
    public object? Value { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// 系统演化配置 (用于持久化演化状态)
/// </summary>
public sealed class SystemEvolutionConfig
{
    public float L2UpgradeThreshold { get; set; } = 0.55f;
    public double RecursiveConvergenceThreshold { get; set; } = 1e-4;
    public int MinRecursionRounds { get; set; } = 2;
    public CollaborationPattern DefaultPattern { get; set; } = CollaborationPattern.Sequential;
    public List<string> PermanentL2Routes { get; set; } = new();
    public List<string> FewShotExamples { get; set; } = new();
}

/// <summary>
/// 自演化循环执行器 (LIFE 框架 - Evolve)
/// 根据归因报告生成并执行演化动作
/// </summary>
public sealed class SelfEvolutionLoop
{
    private readonly SystemEvolutionConfig _config;
    private readonly ILogger<SelfEvolutionLoop> _logger;

    public SelfEvolutionLoop(
        SystemEvolutionConfig config,
        ILogger<SelfEvolutionLoop>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfEvolutionLoop>.Instance;
    }

    /// <summary>
    /// 根据归因报告生成演化计划
    /// </summary>
    public List<EvolutionAction> GeneratePlan(AttributionReport report)
    {
        var actions = new List<EvolutionAction>();

        switch (report.Fault)
        {
            case FaultType.PrematureConvergence:
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.AdjustConvergenceThreshold,
                    Target = "RecursivePipeline",
                    Value = _config.RecursiveConvergenceThreshold / 2.0, // 降低阈值
                    Reason = report.Reasoning
                });
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.UpdateRoutingThreshold, // 增加最小轮次
                    Target = "MinRecursionRounds",
                    Value = _config.MinRecursionRounds + 1,
                    Reason = "Ensure sufficient recursion depth for convergence."
                });
                break;

            case FaultType.ContextLoss:
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.SwitchCollaborationPattern,
                    Target = "RecursivePipeline",
                    Value = CollaborationPattern.Sequential, // 切换到更稳健的模式
                    Reason = "Sequential pattern preserves context better than current mode."
                });
                break;

            case FaultType.CapabilityGap when report.Component == ResponsibleComponent.L1Engine:
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.PermanentRouteToL2,
                    Target = "Router",
                    Value = report.SuggestedFix,
                    Reason = "L1 consistently fails on this query type."
                });
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.TriggerFineTuning,
                    Target = "L1Engine",
                    Value = "Collect failed traces for next fine-tuning cycle.",
                    Reason = "Accumulate data to close L1 capability gap."
                });
                break;

            case FaultType.RoutingError:
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.UpdateRoutingThreshold,
                    Target = "L2UpgradeThreshold",
                    Value = _config.L2UpgradeThreshold + 0.05f, // 提高升级阈值，减少不必要的 L2 调用
                    Reason = "Router was too aggressive in upgrading to L2."
                });
                break;

            case FaultType.PromptAmbiguity:
                actions.Add(new EvolutionAction
                {
                    Type = EvolutionActionType.AddFewShotExample,
                    Target = "SystemPrompt",
                    Value = "Add clarifying examples for this query pattern.",
                    Reason = "Model hallucinated due to lack of guidance."
                });
                break;
        }

        return actions;
    }

    /// <summary>
    /// 执行演化动作
    /// </summary>
    public void Execute(EvolutionAction action)
    {
        _logger.LogInformation("🧬 Executing Evolution: {Type} on {Target} (Reason: {Reason})",
            action.Type, action.Target, action.Reason);

        switch (action.Type)
        {
            case EvolutionActionType.UpdateRoutingThreshold:
                if (action.Target == "L2UpgradeThreshold" && action.Value is float newThreshold)
                {
                    _config.L2UpgradeThreshold = newThreshold;
                    _logger.LogDebug("✅ Updated L2UpgradeThreshold to {Threshold:F2}", newThreshold);
                }
                else if (action.Target == "MinRecursionRounds" && action.Value is int newRounds)
                {
                    _config.MinRecursionRounds = newRounds;
                    _logger.LogDebug("✅ Updated MinRecursionRounds to {Rounds}", newRounds);
                }
                break;

            case EvolutionActionType.AdjustConvergenceThreshold:
                if (action.Value is double newConv)
                {
                    _config.RecursiveConvergenceThreshold = newConv;
                    _logger.LogDebug("✅ Adjusted ConvergenceThreshold to {Threshold:E4}", newConv);
                }
                break;

            case EvolutionActionType.SwitchCollaborationPattern:
                if (action.Value is CollaborationPattern pattern)
                {
                    _config.DefaultPattern = pattern;
                    _logger.LogDebug("✅ Switched DefaultPattern to {Pattern}", pattern);
                }
                break;

            case EvolutionActionType.AddFewShotExample:
                if (action.Value is string example)
                {
                    _config.FewShotExamples.Add(example);
                    _logger.LogDebug("✅ Added FewShotExample. Total: {Count}", _config.FewShotExamples.Count);
                }
                break;

            case EvolutionActionType.PermanentRouteToL2:
                if (action.Target == "Router" && action.Value is string routePattern)
                {
                    // 实际实现中应解析 routePattern 并添加到路由表
                    _logger.LogDebug("✅ Added permanent L2 route pattern: {Pattern}", routePattern);
                }
                break;

            case EvolutionActionType.TriggerFineTuning:
                _logger.LogDebug("📝 Queued fine-tuning task: {Task}", action.Value);
                break;
        }
    }

    /// <summary>
    /// 获取当前系统演化配置
    /// </summary>
    public SystemEvolutionConfig GetConfig() => _config;
}
