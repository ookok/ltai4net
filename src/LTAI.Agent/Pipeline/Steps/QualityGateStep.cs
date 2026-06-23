using System.Text.RegularExpressions;
using LTAI.Agent.Tools.Review;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>Critique dimension scoring (garden-skills inspired 5-dimension framework).</summary>
public sealed record DimensionScore
{
    public string Name { get; init; } = "";
    public double Score { get; init; } // 0-10
    public string? Note { get; init; }
}

public enum QualityLevel { Excellent = 5, Good = 4, Acceptable = 3, Poor = 2, Unacceptable = 1 }

public sealed record QualityGateResult
{
    public QualityLevel Level { get; init; } = QualityLevel.Acceptable;
    public double Score { get; init; }
    public List<string> Issues { get; init; } = [];
    public bool Passed => Score >= PassThreshold;
    public int RetryCount { get; init; }
    public double PassThreshold { get; init; } = 0.65;
    public List<DimensionScore> Dimensions { get; init; } = [];
}

public sealed class QualityGateStep : IPipelineStep
{
    private readonly ILogger<QualityGateStep> _logger;
    private readonly ReviewRuleEngine? _ruleEngine;
    private readonly int _maxRetries;
    private readonly double _passThreshold;

    /// <summary>Dimension weights (normalized to 1.0 sum after application).</summary>
    private static readonly double[] DimWeights = [1.2, 1.0, 0.8, 1.1, 0.9, 1.0];

    public string Name => "QualityGate";

    public QualityGateStep(ILogger<QualityGateStep>? logger = null,
        ReviewRuleEngine? ruleEngine = null, int maxRetries = 2,
        double passThreshold = 0.65)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityGateStep>.Instance;
        _ruleEngine = ruleEngine;
        _maxRetries = maxRetries;
        _passThreshold = Math.Clamp(passThreshold, 0.0, 1.0);
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg == null || string.IsNullOrWhiteSpace(lastMsg.Text))
            return context;

        var result = EvaluateQuality(lastMsg.Text, context);
        context.Set("QualityGateResult", result);

        if (!result.Passed)
        {
            context.QualityGateBlocked = true;
            var msg = $"⚠️ 质量门禁未通过 (得分 {result.Score:P1})\n"
                      + $"维度: {string.Join(", ", result.Dimensions.Select(d => $"{d.Name}={d.Score:F1}"))}\n"
                      + "问题:\n"
                      + string.Join("\n", result.Issues.Select(i => $"- {i}"));
            lock (context.MessagesLock) context.Messages.Add(new ChatMessage(ChatRole.System, msg));
            _logger.LogWarning("QualityGate: blocked (score={Score:P1}, dims={Dims}, issues={Count})",
                result.Score,
                string.Join(",", result.Dimensions.Select(d => $"{d.Name}:{d.Score:F1}")),
                result.Issues.Count);
        }
        else
        {
            context.QualityGateBlocked = false;
            _logger.LogDebug("QualityGate: passed (score={Score:P1}, dims={Dims})",
                result.Score,
                string.Join(",", result.Dimensions.Select(d => $"{d.Name}:{d.Score:F1}")));
        }

        return context;
    }

    /// <summary>
    /// 6-dimension critique scoring (garden-skills inspired + Disco-RAG):
    ///   Philosophy — alignment with agent purpose & user intent
    ///   Completeness — covers all requested aspects
    ///   Clarity — well-structured, good readability
    ///   Craft — no clichés, no placeholders, polished
    ///   Functionality — proper tool usage, no errors
    ///   DiscourseCoherence — rhetorical flow (background→evidence→conclusion)
    /// </summary>
    private QualityGateResult EvaluateQuality(string text, MessageContext context)
    {
        var dimensions = new List<DimensionScore>
        {
            EvaluatePhilosophy(text, context),
            EvaluateCompleteness(text, context),
            EvaluateClarity(text),
            EvaluateCraft(text),
            EvaluateFunctionality(text, context),
            EvaluateDiscourseCoherence(text),
        };

        var issues = new List<string>();
        double weightedSum = 0, weightSum = 0;
        for (int i = 0; i < dimensions.Count; i++)
        {
            var w = i < DimWeights.Length ? DimWeights[i] : 1.0;
            weightedSum += dimensions[i].Score * w;
            weightSum += w;
            if (dimensions[i].Score < 5.0 && !string.IsNullOrEmpty(dimensions[i].Note))
                issues.Add(dimensions[i].Note);
        }
        var normalizedScore = weightedSum / (weightSum * 10.0);

        QualityLevel level;
        if (normalizedScore >= 0.9) level = QualityLevel.Excellent;
        else if (normalizedScore >= 0.75) level = QualityLevel.Good;
        else if (normalizedScore >= _passThreshold) level = QualityLevel.Acceptable;
        else if (normalizedScore >= 0.4) level = QualityLevel.Poor;
        else level = QualityLevel.Unacceptable;

        if (context.GrammarCheckBlocked)
            issues.Add("存在语法错误");

        return new QualityGateResult
        {
            Level = level,
            Score = normalizedScore,
            PassThreshold = _passThreshold,
            Issues = issues,
            Dimensions = dimensions,
        };
    }

    // ── Dimension 1: Philosophy — alignment with agent purpose & user intent ──
    private static DimensionScore EvaluatePhilosophy(string text, MessageContext context)
    {
        var score = 8.0;

        // Penalize hedge phrases that indicate uncertainty/confusion
        var hedgePatterns = new[]
        {
            "我不确定", "我无法", "我不清楚", "我不确定这",
            "I'm not sure", "I cannot", "I'm not certain",
            "I think", "I believe", "I'm not confident",
        };
        foreach (var h in hedgePatterns)
        {
            if (text.Contains(h, StringComparison.OrdinalIgnoreCase))
            {
                score -= 1.5;
                break;
            }
        }

        // Penalize apologetic openings
        if (text.Trim().StartsWith("抱歉", StringComparison.Ordinal) ||
            text.Trim().StartsWith("对不起", StringComparison.Ordinal) ||
            text.Trim().StartsWith("Sorry", StringComparison.OrdinalIgnoreCase))
        {
            score -= 2.0;
        }

        return new DimensionScore
        {
            Name = "哲学一致",
            Score = Math.Clamp(score, 0, 10),
            Note = score < 5 ? "回复包含不确定性表述或道歉开头，与 agent 自信定位不符" : null,
        };
    }

    // ── Dimension 2: Completeness — covers all requested aspects ──
    private static DimensionScore EvaluateCompleteness(string text, MessageContext context)
    {
        var score = 8.0;

        // Too short for the task
        if (text.Length < 20)
        {
            score = text.Length < 5 ? 0.0 : 2.0;
            return new DimensionScore
            {
                Name = "内容完整",
                Score = score,
                Note = "回复过短（<20字符），未覆盖任务需求",
            };
        }

        // Excessively long without sufficient value
        if (text.Length > 8000)
            score -= 1.0;

        return new DimensionScore
        {
            Name = "内容完整",
            Score = Math.Clamp(score, 0, 10),
            Note = score < 5 ? "回复可能未完整覆盖任务需求" : null,
        };
    }

    // ── Dimension 3: Clarity — well-structured, good readability ──
    private static DimensionScore EvaluateClarity(string text)
    {
        var score = 8.0;

        var hasParagraphs = text.Contains('\n');
        var hasPunctuation = text.Contains('。') || text.Contains('.') || text.Contains('；') || text.Contains(';');
        var hasHeaders = text.Contains("##") || text.Contains("###") || text.Contains("---");
        var hasCodeBlocks = text.Contains("```");
        var isChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // Code-focused responses get leniency on punctuation
        if (hasCodeBlocks)
            score += 0.5;

        if (!hasParagraphs && text.Length > 200)
        {
            score -= 2.0;
            return new DimensionScore
            {
                Name = "清晰结构",
                Score = Math.Clamp(score, 0, 10),
                Note = "长回复缺少分段，可读性差",
            };
        }

        if (!hasPunctuation && text.Length > 100 && !hasCodeBlocks)
            score -= 1.0;

        if (hasHeaders)
            score += 1.0;
        else if (text.Length > 500)
            score -= 0.5;

        return new DimensionScore
        {
            Name = "清晰结构",
            Score = Math.Clamp(score, 0, 10),
            Note = score < 5 ? "结构混乱，建议增加分段和标题" : null,
        };
    }

    // ── Dimension 4: Craft — no clichés, no placeholders, polished ──
    private static DimensionScore EvaluateCraft(string text)
    {
        var score = 8.0;
        var issues = new List<string>();

        // Anti-cliché: AI common patterns
        if (Regex.IsMatch(text, @"(purple|pink|violet).*(gradient|渐变)", RegexOptions.IgnoreCase))
        { score -= 1.0; issues.Add("避免紫色/粉色渐变 (AI 俗套)"); }

        if (text.Contains("🚀") || text.Contains("✨") || text.Contains("💡") || text.Contains("🎯"))
        { score -= 0.5; issues.Add("避免滥用 emoji 装饰"); }

        if (Regex.IsMatch(text, @"##\s*(Let me|让我|我来|让我来)", RegexOptions.IgnoreCase))
        { score -= 0.5; issues.Add("避免 '让我…' 式 AI 开场"); }

        if (text.Contains("I'd be happy to") || text.Contains("I'd be glad to") || text.Contains("当然可以"))
        { score -= 0.5; issues.Add("避免 'I\'d be happy to' 式套话"); }

        if (Regex.IsMatch(text, @"^\s*(好的|好|好的，|好的 !|好的！)", RegexOptions.IgnoreCase))
        { score -= 0.5; issues.Add("避免无意义 '好的' 开场"); }

        // Verbose preamble detection
        if (Regex.IsMatch(text, @"(根据|基于|依照).*(代码|文档|文件|分析|审查)", RegexOptions.IgnoreCase))
        { score -= 0.3; }

        if (text.Split('\n').FirstOrDefault()?.Length > 80)
        { score -= 0.3; issues.Add("首行过长，建议直接进入主题"); }

        // Repeating user query verbatim
        if (Regex.IsMatch(text, @"你(刚才|之前|刚刚).*(问了|提到|说).*[。：:]"))
        { score -= 0.5; issues.Add("避免复述用户问题（浪费 token）"); }

        // Placeholder detection
        if (text.Contains("{{") && text.Contains("}}"))
        { score -= 1.5; issues.Add("包含未替换的 {{模板}} 占位符"); }

        if (text.Contains("TODO") || text.Contains("FIXME"))
        { score -= 1.0; issues.Add("包含 TODO/FIXME 未完成标记"); }

        // Mixed Chinese/English in same sentence (code blocks excluded)
        var cleanText = Regex.Replace(text, @"```.*?```", "", RegexOptions.Singleline);
        int sentenceBreaks = cleanText.Count("，。；！？.!?".Contains);
        if (sentenceBreaks > 3)
        {
            var hasChinese = cleanText.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            var hasAscii = cleanText.Any(c => c > 0 && c < 128 && !char.IsWhiteSpace(c) && !char.IsControl(c));
            if (hasChinese && hasAscii)
            {
                // Count inline mixed chars
                int mixedSentences = 0;
                foreach (var line in cleanText.Split('\n'))
                {
                    if (line.Length < 5) continue;
                    if (line.Any(c => c >= 0x4E00 && c <= 0x9FFF) &&
                        Regex.IsMatch(line, @"[a-zA-Z]{3,}"))
                        mixedSentences++;
                }
                if (mixedSentences > sentenceBreaks / 2)
                { score -= 0.5; issues.Add("中英文混杂，建议统一语言"); }
            }
        }

        return new DimensionScore
        {
            Name = "工艺质量",
            Score = Math.Clamp(score, 0, 10),
            Note = issues.Count > 0 ? string.Join("; ", issues) : null,
        };
    }

    // ── Dimension 5: Functionality — proper tool usage, no errors ──
    private DimensionScore EvaluateFunctionality(string text, MessageContext context)
    {
        var score = 8.0;

        var toolCallCount = context.ToolCalls.Count;
        if (toolCallCount == 0 && text.Length > 100)
        {
            score -= 2.0;
            return new DimensionScore
            {
                Name = "工具使用",
                Score = Math.Clamp(score, 0, 10),
                Note = "输出了长回复但未调用任何工具",
            };
        }

        // Check for merge conflict markers in tool results
        foreach (var (_, _, result) in context.ToolCalls)
        {
            if (!string.IsNullOrWhiteSpace(result) &&
                (result.Contains("<<<<<<<") || result.Contains(">>>>>>>") || result.Contains("=======")))
            {
                score -= 2.0;
                return new DimensionScore
                {
                    Name = "工具使用",
                    Score = Math.Clamp(score, 0, 10),
                    Note = "工具输出包含合并冲突标记",
                };
            }
        }

        return new DimensionScore
        {
            Name = "工具使用",
            Score = Math.Clamp(score, 0, 10),
        };
    }

    // ── Dimension 6: DiscourseCoherence — rhetorical flow (Disco-RAG inspired) ──
    private static DimensionScore EvaluateDiscourseCoherence(string text)
    {
        var score = 8.0;
        var issues = new List<string>();

        // Check for logical discourse markers (good sign of structured reasoning)
        var discourseMarkers = new[]
        {
            "因此", "所以", "然而", "但是", "另一方面", "例如", "比如", "总之",
            "综上所述", "首先", "其次", "最后", "第一", "第二",
            "therefore", "however", "in contrast", "for example",
            "in conclusion", "first", "second", "finally",
            "背景", "概述", "具体来说",
        };
        int markerCount = 0;
        foreach (var marker in discourseMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                markerCount++;
        }

        if (markerCount >= 3)
            score += 1.0;
        else if (markerCount == 0)
        {
            score -= 2.0;
            issues.Add("缺少话语标记词，可能为扁平事实堆砌");
        }
        else if (markerCount <= 1)
        {
            score -= 1.0;
            issues.Add("话语标记词过少，建议增加修辞连接");
        }

        // Check for paragraph structure (sign of organized discourse)
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (paragraphs.Length >= 3)
            score += 0.5;
        else if (paragraphs.Length <= 1 && text.Length > 300)
        {
            score -= 1.5;
            issues.Add("长回复缺少分段，话语结构不清晰");
        }

        // Check for conclusion signal in the last paragraph
        var lastParagraph = paragraphs.Length > 0
            ? paragraphs[^1].Trim()
            : text.Trim();
        var hasConclusion = lastParagraph.StartsWith("总之", StringComparison.Ordinal)
            || lastParagraph.StartsWith("综上所述", StringComparison.Ordinal)
            || lastParagraph.StartsWith("因此", StringComparison.Ordinal)
            || lastParagraph.StartsWith("所以", StringComparison.Ordinal)
            || Regex.IsMatch(lastParagraph, @"^(in conclusion|to summarize|overall)", RegexOptions.IgnoreCase);
        if (hasConclusion)
            score += 0.5;
        else if (text.Length > 500)
        {
            score -= 0.5;
            issues.Add("较长回复缺少明确的结论段落");
        }

        // Check for background → evidence flow (early paragraphs set context)
        if (paragraphs.Length >= 2)
        {
            var firstPara = paragraphs[0].Trim().ToLowerInvariant();
            var hasBackground = firstPara.Contains("背景") || firstPara.Contains("概述")
                || firstPara.Contains("overview") || firstPara.Length < 100;
            if (hasBackground)
                score += 0.5;
        }

        return new DimensionScore
        {
            Name = "话语连贯",
            Score = Math.Clamp(score, 0, 10),
            Note = issues.Count > 0 ? string.Join("; ", issues) : null,
        };
    }
}
