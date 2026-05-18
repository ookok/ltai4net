using System.Text.RegularExpressions;

namespace LTAI.AI.Utilities;

public static class GovernorUtilities
{
    // ── Intent Classification ──

    public static (float Complexity, string Label) ClassifyIntent(string query)
    {
        var q = query.Trim();
        int len = q.Length;
        float complexity;
        string label;

        bool isCodeRelated = Regex.IsMatch(q, @"(?:code|代码|function|class|def|bug|error|编译|compile|refactor|重构|implement|实现)",
            RegexOptions.IgnoreCase);
        bool isLongForm = len > 200 || q.Count(c => c == '\n') > 3;
        bool isMultiPart = Regex.IsMatch(q, @"(?:首先.*然后|第一.*第二|步骤|step\s*\d|1\.\s.*2\.\s)",
            RegexOptions.IgnoreCase);
        bool isSimple = Regex.IsMatch(q, @"^(你好|hi|hello|谢谢|bye|再见|什么是|what is|how to|如何|怎么)",
            RegexOptions.IgnoreCase) && len < 50;

        if (isSimple && len < 50)
        {
            complexity = 0.2f + Math.Min(len / 500f, 0.3f);
            label = complexity > 0.4f ? "deep" : "fast";
        }
        else if (isLongForm || isMultiPart)
        {
            complexity = Math.Min(0.6f + len / 2000f, 1.0f);
            label = "deep";
        }
        else if (isCodeRelated)
        {
            complexity = 0.5f + Math.Min(len / 1000f, 0.5f);
            label = "deep";
        }
        else
        {
            complexity = 0.3f + Math.Min(len / 1500f, 0.5f);
            label = complexity > 0.5f ? "deep" : "fast";
        }

        return (MathF.Round(complexity, 2), label);
    }

    // ── Emotion Detection ──

    private static readonly (string Pattern, string Emotion)[] EmotionPatterns =
    {
        (@"(?:急|快|马上|立即|赶紧|urgent|asap|立刻|紧急)", "urgent"),
        (@"(?:气死|愤怒|生气|fuck|shit|垃圾|坑|骗子|angry|mad)", "angry"),
        (@"(?:不明白|不懂|困惑|confused|不理解|啥意思|什么意思|茫然)", "confused"),
        (@"(?:好奇|想知道|探索|how|怎么样|why|为什么|兴趣)", "curious"),
        (@"(?:[😊😄😂😍👍🎉]|哈哈|开心|谢谢|不错|好|great|nice|awesome|棒|赞|厉害|cool)", "positive"),
        (@"(?:难过|伤心|[😢😭]|sad|失望|沮丧|唉|sigh|悲剧|遗憾)", "sad"),
        (@"(?:担心|害怕|危险|风险|worry|fear|scared|威胁|恐怕)", "anxious"),
        (@"(?:好累|困|tired|疲惫|厌倦|无聊|boring)", "tired"),
    };

    public static string DetectEmotion(string query)
    {
        var q = query.ToLowerInvariant();

        if (q.Length < 10) return "neutral";

        int negativeWords = Regex.Matches(q, @"(?:不|没|别|not|no|never|don't|cannot|无法|不能|错误|失败|error|fail|bug)").Count;
        int positiveWords = Regex.Matches(q, @"(?:好|great|nice|excellent|完美|成功|success|顺利)").Count;

        foreach (var (pattern, emotion) in EmotionPatterns)
        {
            if (Regex.IsMatch(q, pattern, RegexOptions.IgnoreCase))
                return emotion;
        }

        if (negativeWords > positiveWords && negativeWords >= 2) return "negative";
        if (positiveWords > negativeWords && positiveWords >= 2) return "positive";

        if (q.EndsWith("?") || q.EndsWith("？") || q.Contains("?")) return "curious";
        if (q.EndsWith("!") || q.EndsWith("！") || q.Contains("请") || q.Contains("please")) return "engaged";

        return "neutral";
    }

    // ── Hallucination Detection ──

    public static (bool IsHallucinated, string Reason) CheckHallucination(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return (false, "empty");

        var issues = new List<string>();

        if (Regex.IsMatch(response, @"(?:(?:我|we)\s*(?:不确定|not sure|不知道|don't know|可能|maybe|perhaps|大概|或许|估计))",
                RegexOptions.IgnoreCase))
            issues.Add("expresses_uncertainty");

        if (Regex.IsMatch(response, @"(?:(?:根据我的知识|据我所知|as of my|我的训练数据|up to my knowledge|训练截止))",
                RegexOptions.IgnoreCase))
            issues.Add("knowledge_cutoff_disclaimer");

        var vaguePatterns = new[] { "大概", "或许", "可能", "approximately", "around", "about", "roughly", "估计" };
        int vagueCount = vaguePatterns.Count(p =>
            response.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (vagueCount >= 3) issues.Add("excessive_vagueness");

        if (!Regex.IsMatch(response, @"[\u4e00-\u9fff]") && Regex.IsMatch(response, @"[a-zA-Z]{5,}"))
        {
            double alphaRatio = (double)Regex.Matches(response, @"[a-zA-Z]").Count / Math.Max(response.Length, 1);
            if (alphaRatio < 0.3) issues.Add("low_alpha_density");
        }

        var contradictions = new[]
        {
            (@"(?i)\byes\b.*\bno\b", "yes_no_contradiction"),
            (@"(?i)\btrue\b.*\bfalse\b", "true_false_contradiction"),
            (@"是.*否|对.*错|正确.*错误", "contradiction_zh"),
        };
        foreach (var (pattern, reason) in contradictions)
        {
            if (Regex.IsMatch(response, pattern))
                issues.Add(reason);
        }

        if (issues.Count == 0) return (false, "clean");
        return (issues.Count >= 2, string.Join("; ", issues));
    }

    // ── Task Decomposition ──

    public static string[] DecomposeTask(string taskDescription)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
            return Array.Empty<string>();

        var parts = Regex.Split(taskDescription, @"(?:(?:并且|以及|然后|接着|之后|同时|and|then|also)\s*)");
        if (parts.Length > 1 && parts.All(p => p.Trim().Length > 3))
            return parts.Select(p => p.Trim()).Where(p => p.Length > 3).ToArray();

        parts = Regex.Split(taskDescription, @"\s*(?:[；;]|(?:\r?\n))\s*");
        if (parts.Length > 1 && parts.All(p => p.Trim().Length > 3))
            return parts.Select(p => p.Trim()).Where(p => p.Length > 3).ToArray();

        var steps = new List<string>();
        foreach (Match m in Regex.Matches(taskDescription, @"(?:Step\s*\d+|步骤\s*\d+|[1-9]\d*[.)]\s*)([^.。]+)[.。]?"))
        {
            var step = m.Groups[1].Value.Trim();
            if (step.Length > 3) steps.Add(step);
        }
        if (steps.Count > 0) return steps.ToArray();

        return new[] { taskDescription.Trim() };
    }

    // ── Anti-Pattern Detection ──

    public static string[] DetectAntiPatterns(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Array.Empty<string>();

        var detected = new List<(string Pattern, double Confidence)>();

        // Circular dependency
        int importCount = Regex.Matches(code, @"(?:import\s+|using\s+|require\s*\(|from\s+\S+\s+import)").Count;
        if (importCount > 20) detected.Add(("circular_dependency", Math.Min(importCount / 40.0, 1.0)));

        // God module
        int classCount = Regex.Matches(code, @"(?:class\s+\w+|public\s+class\s+\w+)").Count;
        int methodCount = Regex.Matches(code, @"(?:def\s+\w+|public\s+\w+\s+\w+\s*\()").Count;
        int lineCount = code.Count(c => c == '\n');
        if (lineCount > 500 && methodCount > 30) detected.Add(("god_module", Math.Min(lineCount / 2000.0, 1.0)));
        if (classCount == 1 && methodCount > 50 && lineCount > 300) detected.Add(("god_module", Math.Min(methodCount / 100.0, 1.0)));

        // Deep inheritance
        int extendCount = Regex.Matches(code, @"(?:extends\s+\w+|:\s*.*BaseClass|:\s+\w+\s*\{)").Count;
        if (extendCount > 5) detected.Add(("deep_inheritance", Math.Min(extendCount / 15.0, 1.0)));

        // Tight coupling
        int newCount = Regex.Matches(code, @"new\s+\w+\s*\(").Count;
        int externalRefs = Regex.Matches(code, @"(?:import|using)\s+[^.;]+").Count;
        if (externalRefs > 15) detected.Add(("tight_coupling", Math.Min(externalRefs / 40.0, 1.0)));

        // Dead code (commented out blocks)
        int commentBlocks = Regex.Matches(code, @"(?:#|//|/\*)[\s\S]{50,}(?:\*/|$)").Count;
        if (commentBlocks > 5) detected.Add(("dead_code", Math.Min(commentBlocks / 20.0, 1.0)));

        // Hardcoded secrets
        if (Regex.IsMatch(code, @"(?:api[_-]?key|secret|password|token)\s*[:=]\s*['""][^'""]{8,}['""]",
                RegexOptions.IgnoreCase))
            detected.Add(("hardcoded_secret", 0.9));

        return detected.Where(d => d.Confidence > 0.3).Select(d => d.Pattern).Distinct().ToArray();
    }
}
