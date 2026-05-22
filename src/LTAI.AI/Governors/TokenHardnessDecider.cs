using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LTAI.AI.Governors;

/// <summary>
/// Token 级困难度评估结果
/// </summary>
public record TokenHardnessResult
{
    public float Confidence { get; init; }
    public bool IsHard { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// 思考状态机
/// </summary>
public enum ThinkingState
{
    Idle,       // 正常生成
    ReThinking, // L1 内部重思考
    Delegating, // 准备升级 L2
    Verifying   // 闭环验证阶段 (Closed-Loop Verification)
}

/// <summary>
/// Token 级困难度决策器
/// 基于 Log-Probabilities、启发式规则或梯度范数 (PACE) 判断当前 Token 是否需要额外思考
/// </summary>
public sealed class TokenHardnessDecider
{
    private readonly float _hardThreshold;
    private readonly int _maxConsecutiveHard;
    private readonly LearningProgressTracker? _progressTracker;
    private int _consecutiveHardCount;
    private int _totalTokensProcessed;

    public TokenHardnessDecider(
        float hardThreshold = 0.6f, 
        int maxConsecutiveHard = 3,
        LearningProgressTracker? progressTracker = null)
    {
        _hardThreshold = hardThreshold;
        _maxConsecutiveHard = maxConsecutiveHard;
        _progressTracker = progressTracker;
        _consecutiveHardCount = 0;
        _totalTokensProcessed = 0;
    }

    public ThinkingState CurrentState { get; set; } = ThinkingState.Idle;

    /// <summary>
    /// 评估当前 Token 的困难度
    /// 支持三种信号源 (优先级从高到低):
    /// 1. 梯度范数 (PACE): 最准确，直接反映真实学习进度
    /// 2. Log-Probabilities: 中等准确度
    /// 3. 启发式规则: 降级方案
    /// </summary>
    public TokenHardnessResult Evaluate(
        string token, 
        float? logProb = null, 
        string? context = null,
        float? gradientNorm = null)
    {
        _totalTokensProcessed++;
        
        float confidence;
        string reason;
        
        // PACE: 优先使用梯度范数
        if (gradientNorm.HasValue)
        {
            confidence = GradientNormToConfidence(gradientNorm.Value);
            reason = $"Gradient norm: {gradientNorm.Value:E3}";
            
            // 记录学习进度
            if (_progressTracker != null && context != null)
            {
                var queryId = $"token_{token.GetHashCode()}_{context.GetHashCode()}";
                _progressTracker.RecordGradientNorm(queryId, gradientNorm.Value);
            }
        }
        else if (logProb != null)
        {
            confidence = ConvertLogProbToConfidence(logProb.Value);
            reason = $"Log-prob: {logProb.Value:F2}";
        }
        else
        {
            confidence = HeuristicConfidence(token, context);
            reason = "Heuristic";
        }

        bool isHard = confidence < _hardThreshold;

        if (isHard)
        {
            _consecutiveHardCount++;
        }
        else
        {
            _consecutiveHardCount = 0;
        }

        // 状态机转换
        if (_consecutiveHardCount >= _maxConsecutiveHard)
        {
            CurrentState = ThinkingState.Delegating;
        }
        else if (isHard)
        {
            CurrentState = ThinkingState.ReThinking;
        }
        else
        {
            CurrentState = ThinkingState.Idle;
        }

        return new TokenHardnessResult
        {
            Confidence = confidence,
            IsHard = isHard,
            Reason = isHard ? $"{reason} (Hard)" : $"{reason} (Normal)"
        };
    }

    /// <summary>
    /// 将梯度范数转换为置信度 (PACE 理论)
    /// 梯度范数越大 → 学习进度越大 → 任务越难 → 置信度越低
    /// </summary>
    private float GradientNormToConfidence(float gradientNorm)
    {
        // 使用指数衰减映射: confidence = exp(-α * ||∇L||)
        // α 控制衰减速率，默认 0.1
        const float alpha = 0.1f;
        var confidence = MathF.Exp(-alpha * gradientNorm);
        return Math.Max(0.1f, Math.Min(1.0f, confidence));
    }

    /// <summary>
    /// 将 Log-Probability 转换为置信度 (0.0 - 1.0)
    /// </summary>
    private static float ConvertLogProbToConfidence(float logProb)
    {
        // Log-Prob 通常在 [-10, 0] 之间，越接近 0 越确定
        // 使用 Sigmoid 映射
        var normalized = Math.Min(1.0, Math.Max(0.0, Math.Exp(logProb)));
        return (float)normalized;
    }

    /// <summary>
    /// 启发式置信度估算 (当无法获取 Log-Prob 时使用)
    /// 引入 Proxy Prompt 压缩机制，避免长上下文干扰
    /// </summary>
    private static float HeuristicConfidence(string token, string? context)
    {
        float score = 0.85f; // 基础置信度

        // 1. 标点符号、停用词通常很简单
        var simpleTokens = new[] { ".", ",", "!", "?", " ", "\n", "the", "a", "is", "are", "of", "and", "to", "in", "that", "it", "的", "了", "是", "在", "和", "与" };
        if (simpleTokens.Any(s => token.Equals(s, StringComparison.OrdinalIgnoreCase)))
            return 0.95f;

        // 2. 数字、代码符号通常较难
        if (token.Any(char.IsDigit) || token.Any(c => "!@#$%^&*()_+-=[]{}|;':,.<>?/~`".Contains(c)))
            score -= 0.2f;

        // 3. 长单词/生僻词较难
        if (token.Length > 10)
            score -= 0.15f;

        // 4. 上下文复杂度 (使用 Proxy Prompt 压缩)
        if (!string.IsNullOrEmpty(context) && context.Length > 300)
        {
            var proxyPrompt = CompressToProxyPrompt(context);
            
            // 如果 Proxy Prompt 包含大量约束，则增加难度
            var constraintDensity = proxyPrompt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length / (float)Math.Max(1, context.Length / 100);
            score -= Math.Min(0.2f, constraintDensity * 0.05f);
            
            // 长上下文本身增加不确定性
            score -= 0.05f;
        }

        return Math.Max(0.1f, Math.Min(1.0f, score));
    }

    /// <summary>
    /// 将长上下文压缩为 Proxy Prompt (提取关键约束和实体)
    /// 灵感来自 CLVR 的 Proxy Prompt Mechanism
    /// </summary>
    private static string CompressToProxyPrompt(string context)
    {
        var lines = context.Split('\n');
        var keyParts = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // 提取包含约束、条件、定义的短句
            if (trimmed.Contains(':') || trimmed.Contains('=') || 
                trimmed.Contains("必须") || trimmed.Contains("不要") || 
                trimmed.Contains("if") || trimmed.Contains("only"))
            {
                keyParts.Add(trimmed);
            }
        }

        // 返回压缩后的关键约束摘要
        return string.Join("\n", keyParts.Take(5));
    }

    /// <summary>
    /// 重置决策器状态
    /// </summary>
    public void Reset()
    {
        _consecutiveHardCount = 0;
        _totalTokensProcessed = 0;
        CurrentState = ThinkingState.Idle;
    }
}
