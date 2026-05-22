using System;

namespace LTAI.AI.Governors;

/// <summary>
/// 故障类型枚举
/// </summary>
public enum FaultType
{
    /// <summary>模型能力不足以处理当前任务</summary>
    CapabilityGap,
    /// <summary>上下文信息在传递过程中丢失或被截断</summary>
    ContextLoss,
    /// <summary>路由决策错误 (如简单问题路由到 L2)</summary>
    RoutingError,
    /// <summary>RecursiveMAS 提前终止但结果未收敛</summary>
    PrematureConvergence,
    /// <summary>外部工具调用失败</summary>
    ToolFailure,
    /// <summary>用户 Prompt 模糊导致模型幻觉</summary>
    PromptAmbiguity
}

/// <summary>
/// 责任组件枚举
/// </summary>
public enum ResponsibleComponent
{
    L1Engine,
    L2Engine,
    Router,
    RecursivePipeline,
    Verifier,
    ToolExecutor
}

/// <summary>
/// 任务执行轨迹 (用于归因分析)
/// </summary>
public sealed record TaskTrace
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public bool VerificationPassed { get; init; }
    public string VerificationReason { get; init; } = "";
    public string Route { get; init; } = "";
    public float Complexity { get; init; }
    public int RecursionRoundsUsed { get; init; }
    public int RecursionRoundsPlanned { get; init; }
    public double DeltaNorm { get; init; }
    public LearningStatus LearningStatus { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// 归因分析报告
/// </summary>
public sealed record AttributionReport
{
    public FaultType Fault { get; init; }
    public ResponsibleComponent Component { get; init; }
    public float Confidence { get; init; }
    public string Reasoning { get; init; } = "";
    public string SuggestedFix { get; init; } = "";
}

/// <summary>
/// 故障归因引擎 (LIFE 框架 - Find Faults)
/// 分析失败的任务轨迹，输出结构化的归因报告
/// </summary>
public sealed class FailureAttributionEngine
{
    /// <summary>
    /// 分析任务轨迹并生成归因报告
    /// </summary>
    public AttributionReport Analyze(TaskTrace trace)
    {
        if (trace.VerificationPassed)
        {
            return new AttributionReport
            {
                Fault = FaultType.CapabilityGap, // Placeholder, should not be called on success
                Component = ResponsibleComponent.L1Engine,
                Confidence = 0,
                Reasoning = "Task succeeded, no attribution needed.",
                SuggestedFix = "None"
            };
        }

        // 1. 检查是否由于提前收敛导致失败
        if (trace.Route.Contains("recursive") && trace.RecursionRoundsUsed < trace.RecursionRoundsPlanned)
        {
            if (trace.DeltaNorm > 1e-4) // 参数变化仍较大，说明未真正收敛
            {
                return new AttributionReport
                {
                    Fault = FaultType.PrematureConvergence,
                    Component = ResponsibleComponent.RecursivePipeline,
                    Confidence = 0.85f,
                    Reasoning = $"Recursive loop stopped early ({trace.RecursionRoundsUsed}/{trace.RecursionRoundsPlanned}) but ||Δθ||²={trace.DeltaNorm:E4} indicates non-convergence.",
                    SuggestedFix = "Lower convergence threshold or increase min recursion rounds."
                };
            }
        }

        // 2. 检查上下文丢失 (关键词覆盖率低)
        if (trace.VerificationReason.Contains("Low keyword coverage", StringComparison.OrdinalIgnoreCase))
        {
            if (trace.Route.Contains("recursive"))
            {
                return new AttributionReport
                {
                    Fault = FaultType.ContextLoss,
                    Component = ResponsibleComponent.RecursivePipeline,
                    Confidence = 0.75f,
                    Reasoning = "Significant semantic drift detected during latent space transfer.",
                    SuggestedFix = "Switch to Sequential pattern or increase context retention in RecursiveLink."
                };
            }
            else
            {
                return new AttributionReport
                {
                    Fault = FaultType.ContextLoss,
                    Component = ResponsibleComponent.L1Engine,
                    Confidence = 0.70f,
                    Reasoning = "Model failed to retain key constraints from prompt.",
                    SuggestedFix = "Add explicit constraint reinforcement or increase context window."
                };
            }
        }

        // 3. 检查能力缺口 (Capability Gap)
        if (trace.LearningStatus == LearningStatus.OutOfDistribution || trace.Complexity > 0.8f)
        {
            if (trace.Route.Contains("local_llm") || trace.Route.Contains("recursive"))
            {
                return new AttributionReport
                {
                    Fault = FaultType.CapabilityGap,
                    Component = ResponsibleComponent.L1Engine,
                    Confidence = 0.90f,
                    Reasoning = $"Query complexity ({trace.Complexity:F2}) exceeds L1 capacity. OOD detected.",
                    SuggestedFix = "Permanently route similar queries to L2 or trigger L1 fine-tuning."
                };
            }
            else if (trace.Route.Contains("delegate_l2"))
            {
                return new AttributionReport
                {
                    Fault = FaultType.CapabilityGap,
                    Component = ResponsibleComponent.L2Engine,
                    Confidence = 0.80f,
                    Reasoning = "Even L2 failed to produce satisfactory result.",
                    SuggestedFix = "Query is beyond current system capabilities. Requires external tool or human intervention."
                };
            }
        }

        // 4. 检查路由错误 (高置信度但结果差)
        if (trace.Complexity < 0.3f && !trace.Route.Contains("cache") && !trace.Route.Contains("reflex"))
        {
            return new AttributionReport
            {
                Fault = FaultType.RoutingError,
                Component = ResponsibleComponent.Router,
                Confidence = 0.85f,
                Reasoning = $"Simple query (complexity={trace.Complexity:F2}) was routed to expensive path ({trace.Route}).",
                SuggestedFix = "Adjust router thresholds or add to reflex/cache list."
            };
        }

        // 5. 默认：提示模糊或幻觉
        return new AttributionReport
        {
            Fault = FaultType.PromptAmbiguity,
            Component = ResponsibleComponent.L1Engine,
            Confidence = 0.60f,
            Reasoning = "No specific fault pattern matched. Likely hallucination due to ambiguous prompt.",
            SuggestedFix = "Request clarification from user or add few-shot examples."
        };
    }
}
