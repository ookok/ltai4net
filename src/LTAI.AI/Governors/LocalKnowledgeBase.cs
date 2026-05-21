using System.Collections.Concurrent;

namespace LTAI.AI.Governors;

public sealed record LocalAnswerResult
{
    public string Answer { get; init; } = "";
    public float Confidence { get; init; }
}

public sealed class LocalKnowledgeBase
{
    private readonly ConcurrentDictionary<string, LocalAnswerResult> _exactAnswers = new();
    private readonly ConcurrentDictionary<string, LocalAnswerResult> _patternAnswers = new();
    private readonly ConcurrentDictionary<string, LearnedPattern> _learnedPatterns = new();

    public LocalKnowledgeBase()
    {
        SeedBuiltInKnowledge();
    }

    public LocalAnswerResult? TryAnswer(string query)
    {
        var trimmed = query.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (_exactAnswers.TryGetValue(lower, out var exact))
            return exact;

        foreach (var (pattern, result) in _patternAnswers)
        {
            if (lower.Contains(pattern))
                return result;
        }

        return null;
    }

    public LocalAnswerResult? MatchPattern(string query)
    {
        var lower = query.ToLowerInvariant();

        foreach (var (key, learned) in _learnedPatterns)
        {
            if (lower.Contains(key))
                return new LocalAnswerResult { Answer = learned.SimplifiedAnswer, Confidence = learned.Confidence };
        }

        return null;
    }

    public void AddLearnedPattern(string query, string simplifiedAnswer, string keyConcepts)
    {
        var lower = query.ToLowerInvariant();
        var keywords = ExtractKeywords(lower);

        foreach (var kw in keywords)
        {
            _learnedPatterns[kw] = new LearnedPattern
            {
                SimplifiedAnswer = simplifiedAnswer,
                KeyConcepts = keyConcepts,
                Confidence = 0.3f,
                UsageCount = 0
            };
        }
    }

    public int KnowledgeCount => _exactAnswers.Count + _patternAnswers.Count + _learnedPatterns.Count;

    private void SeedBuiltInKnowledge()
    {
        var greetings = new[] { "你好", "hello", "hi", "hey" };
        foreach (var g in greetings)
            _exactAnswers[g] = new LocalAnswerResult { Answer = "你好！有什么我可以帮你的？", Confidence = 0.9f };

        var thanks = new[] { "谢谢", "thanks", "thank you", "多谢" };
        foreach (var t in thanks)
            _exactAnswers[t] = new LocalAnswerResult { Answer = "不客气！如果还有其他问题，随时问我。", Confidence = 0.95f };

        var bye = new[] { "再见", "bye", "拜拜" };
        foreach (var b in bye)
            _exactAnswers[b] = new LocalAnswerResult { Answer = "再见！有需要随时找我。", Confidence = 0.9f };

        _exactAnswers["你是谁"] = new LocalAnswerResult { Answer = "我是 LivingTree AI Agent，一个基于生物启发式治理架构的智能助手。", Confidence = 0.85f };
        _exactAnswers["what are you"] = new LocalAnswerResult { Answer = "I'm LivingTree AI Agent, an intelligent assistant built on bio-inspired governance architecture.", Confidence = 0.85f };
        _exactAnswers["你能做什么"] = new LocalAnswerResult { Answer = "我可以帮你：写代码、分析问题、解释概念、设计架构、审查代码、规划方案等。请告诉我你的具体需求。", Confidence = 0.8f };
        _exactAnswers["今天天气怎么样"] = new LocalAnswerResult { Answer = "我无法获取实时天气信息。你可以使用天气查询工具或查看天气网站。", Confidence = 0.7f };

        _patternAnswers["什么是"] = new LocalAnswerResult { Answer = "这是一个概念解释问题。让我为你详细说明。", Confidence = 0.3f };
        _patternAnswers["怎么用"] = new LocalAnswerResult { Answer = "这是一个使用指导问题。让我帮你了解具体用法。", Confidence = 0.3f };
        _patternAnswers["how to"] = new LocalAnswerResult { Answer = "这是一个操作指导问题。让我为你逐步说明。", Confidence = 0.3f };
    }

    private static string[] ExtractKeywords(string text)
    {
        var stopWords = new HashSet<string>
        {
            "的", "了", "是", "在", "我", "有", "和", "就", "不", "人", "都", "一", "一个",
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could", "should",
            "to", "of", "in", "for", "on", "with", "at", "by", "from", "this", "that"
        };

        var words = text.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Where(w => w.Length > 1 && !stopWords.Contains(w.ToLowerInvariant())).Distinct().Take(5).ToArray();
    }

    private sealed record LearnedPattern
    {
        public string SimplifiedAnswer { get; init; } = "";
        public string KeyConcepts { get; init; } = "";
        public float Confidence { get; init; }
        public int UsageCount { get; init; }
    }
}
