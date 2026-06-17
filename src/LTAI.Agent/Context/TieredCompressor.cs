using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Agent.Context;

public enum CompressTier
{
    LowPriority,
    Normal,
    Recent,
    Critical
}

/// <summary>
/// Conversation type classification for predictive compression.
/// Inspired by FlashMemory-DeepSeek-V4's lookahead paradigm: predict
/// which context blocks will be needed based on conversation type,
/// rather than applying uniform compression ratios.
/// </summary>
public enum ConversationType
{
    /// <summary>General chat — default tiered compression.</summary>
    General,
    /// <summary>Code review — preserve recent code/output pairs.</summary>
    CodeReview,
    /// <summary>Debugging/fix — preserve error messages, stack traces, hypotheses.</summary>
    Debugging,
    /// <summary>Task/goal planning — preserve requirements, constraints, decisions.</summary>
    Planning,
    /// <summary>Q&A / knowledge lookup — preserve question-answer chains.</summary>
    QandA,
    /// <summary>Architecture/design discussion — preserve diagrams, trade-offs.</summary>
    Design,
    /// <summary>Document/office editing — preserve document content.</summary>
    DocumentEditing,
}

public sealed class TieredCompressor
{
    // ── Default base ratios ──
    private static readonly Dictionary<CompressTier, double> BaseRatios = new()
    {
        [CompressTier.LowPriority] = 0.3,
        [CompressTier.Normal] = 0.5,
        [CompressTier.Recent] = 0.7,
        [CompressTier.Critical] = 0.95,
    };

    // ── Predictive boost by conversation type ──
    // Each conversation type can boost specific tiers by a multiplier
    // (capped at 1.0 max ratio). Configurable via LTAI_COMPRESS_BOOST_*
    // environment variables.
    private static readonly Dictionary<ConversationType, Dictionary<CompressTier, double>> TypeBoosts = LoadTypeBoosts();

    private static Dictionary<ConversationType, Dictionary<CompressTier, double>> LoadTypeBoosts()
    {
        var defaults = new Dictionary<ConversationType, Dictionary<CompressTier, double>>
        {
            [ConversationType.CodeReview] = new()
            {
                [CompressTier.Recent] = 0.15,
                [CompressTier.Critical] = 0.05,
            },
            [ConversationType.Debugging] = new()
            {
                [CompressTier.Recent] = 0.20,
                [CompressTier.Critical] = 0.05,
                [CompressTier.Normal] = 0.10,
            },
            [ConversationType.Planning] = new()
            {
                [CompressTier.Critical] = 0.05,
                [CompressTier.Recent] = 0.10,
            },
            [ConversationType.QandA] = new()
            {
                [CompressTier.Recent] = 0.10,
                [CompressTier.Normal] = 0.10,
            },
            [ConversationType.Design] = new()
            {
                [CompressTier.Critical] = 0.05,
                [CompressTier.Recent] = 0.10,
            },
            [ConversationType.DocumentEditing] = new()
            {
                [CompressTier.Recent] = 0.15,
                [CompressTier.Normal] = 0.10,
            },
        };

        // Env override: LTAI_COMPRESS_BOOST_CodeReview_Recent=0.25
        foreach (var (type, tierBoosts) in defaults)
        {
            foreach (var (tier, _) in tierBoosts.ToList())
            {
                var envKey = $"LTAI_COMPRESS_BOOST_{type}_{tier}";
                var envVal = Environment.GetEnvironmentVariable(envKey);
                if (double.TryParse(envVal, out var parsed) && parsed >= 0 && parsed <= 1)
                    defaults[type][tier] = parsed;
            }
        }

        return defaults;
    }

    private ConversationType _lastType = ConversationType.General;

    public ConversationType LastDetectedType => _lastType;

    /// <summary>
    /// Classify message index into a compression tier.
    /// Recent messages (higher index) get higher retention; older ones compress more.
    /// </summary>
    public CompressTier Classify(int index, int totalCount)
    {
        if (totalCount <= 5) return CompressTier.Critical;
        var pct = (double)index / totalCount;
        return pct switch
        {
            < 0.25 => CompressTier.Critical,
            < 0.50 => CompressTier.Recent,
            < 0.75 => CompressTier.Normal,
            _ => CompressTier.LowPriority
        };
    }

    /// <summary>
    /// Get compression ratio for a tier, adjusted by the predicted conversation type.
    /// Higher ratio = more content retained (less compression).
    /// </summary>
    public double GetCompressionRatio(CompressTier tier, ConversationType? typeOverride = null)
    {
        var type = typeOverride ?? _lastType;
        var baseRatio = BaseRatios.GetValueOrDefault(tier, 0.6);

        if (TypeBoosts.TryGetValue(type, out var boosts) &&
            boosts.TryGetValue(tier, out var boost))
        {
            return Math.Min(1.0, baseRatio + boost);
        }

        return baseRatio;
    }

    /// <summary>
    /// Detect conversation type from recent message text.
    /// Uses keyword patterns — lightweight, no embedding needed.
    /// Call this periodically (e.g., every N turns) to update prediction.
    /// </summary>
    public ConversationType DetectType(IReadOnlyList<string> recentMessages)
    {
        if (recentMessages == null || recentMessages.Count == 0)
            return ConversationType.General;

        var text = string.Join("\n", recentMessages);
        var lower = text.ToLowerInvariant();

        // Score each type by keyword matches
        var scores = new Dictionary<ConversationType, int>();

        ScoreType(scores, ConversationType.CodeReview, lower,
            ["review", "code review", "cr:", "lgtm", "nit:", "refactor", "代码审查", "code style",
             "pull request", "pr:", "diff", "改动", "review this"]);

        ScoreType(scores, ConversationType.Debugging, lower,
            ["bug", "error", "exception", "crash", "fix", "stack trace", "debug", "nullreference",
             "失败", "错误", "异常", "崩溃", "调试", "排查", "not working", "broken",
             "fails", "throwing", "unexpected", "修复"]);

        ScoreType(scores, ConversationType.Planning, lower,
            ["plan", "task", "todo", "sprint", "milestone", "goal", "requirement", "constraint",
             "计划", "任务", "目标", "需求", "待办", "方案", "设计", "implement",
             "step", "phase", "roadmap", "路线图"]);

        ScoreType(scores, ConversationType.QandA, lower,
            ["what is", "how to", "how do", "why does", "what does", "explain",
             "是什么意思", "怎么做", "为什么", "如何", "区别", "对比",
             "difference between", "vs", "versus"]);

        ScoreType(scores, ConversationType.Design, lower,
            ["architecture", "design", "pattern", "trade-off", "decision", "proposal",
             "架构", "设计模式", "方案对比", "权衡", "提议", "system design",
             "component", "interface", "模块"]);

        ScoreType(scores, ConversationType.DocumentEditing, lower,
            ["document", "doc", "write", "edit", "draft", "format", "word", "excel", "ppt",
             "文档", "编写", "编辑", "起草", "格式", "排版"]);

        var best = scores
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault(kv => kv.Value > 0);

        var detected = best.Key;
        _lastType = detected;

        return detected;
    }

    private static void ScoreType(
        Dictionary<ConversationType, int> scores,
        ConversationType type, string lower,
        string[] keywords)
    {
        var count = keywords.Count(kw => lower.Contains(kw, StringComparison.OrdinalIgnoreCase));
        if (count > 0)
            scores[type] = count;
    }

    /// <summary>
    /// Summarize tier stats with predictive type info.
    /// </summary>
    public string SummarizeTierStats(int totalMessages, int compressedCount, int lowPriorityCount)
    {
        var typeLabel = _lastType switch
        {
            ConversationType.CodeReview => "代码审查",
            ConversationType.Debugging => "调试",
            ConversationType.Planning => "任务规划",
            ConversationType.QandA => "问答",
            ConversationType.Design => "架构设计",
            ConversationType.DocumentEditing => "文档编辑",
            _ => "通用",
        };

        return $"压缩统计: {compressedCount}/{totalMessages} 条消息 | " +
               $"低优先级: {lowPriorityCount} | " +
               $"预测类型: {typeLabel} | " +
               $"目标压缩率: 低优先级{GetCompressionRatio(CompressTier.LowPriority):P0} / " +
               $"普通{GetCompressionRatio(CompressTier.Normal):P0} / " +
               $"近期{GetCompressionRatio(CompressTier.Recent):P0} / " +
               $"关键{GetCompressionRatio(CompressTier.Critical):P0}";
    }
}
