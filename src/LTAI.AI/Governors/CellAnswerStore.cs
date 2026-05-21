using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record CellAnswer
{
    public string Pattern { get; init; } = "";
    public string Answer { get; init; } = "";
    public float Confidence { get; init; }
    public int HitCount { get; init; }
    public DateTime LastHit { get; init; }
}

public sealed record CellAnswerResult
{
    public bool Found { get; init; }
    public string Answer { get; init; } = "";
    public float Confidence { get; init; }
    public string MatchedPattern { get; init; } = "";
}

public sealed class CellAnswerStore
{
    private readonly Dictionary<string, List<CellAnswer>> _answers = new();
    private readonly ILogger<CellAnswerStore> _logger;
    private readonly object _lock = new();

    public CellAnswerStore(ILogger<CellAnswerStore>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CellAnswerStore>.Instance;
        InitializeDefaultAnswers();
    }

    private void InitializeDefaultAnswers()
    {
        var defaults = new Dictionary<string, List<CellAnswer>>
        {
            ["greeting"] = new()
            {
                new() { Pattern = "hello|hi|hey|你好|早上好|晚上好", Answer = "你好！有什么我可以帮助你的吗？", Confidence = 0.95f },
                new() { Pattern = "how are you|你好吗|最近怎么样", Answer = "我运行良好，感谢关心！随时准备帮助你。", Confidence = 0.9f },
                new() { Pattern = "thank|谢谢|感谢", Answer = "不客气！如果还有其他问题，随时问我。", Confidence = 0.95f },
                new() { Pattern = "bye|再见|拜拜", Answer = "再见！祝你一切顺利。", Confidence = 0.95f },
            },
            ["code"] = new()
            {
                new() { Pattern = "what is.*function|函数.*什么", Answer = "函数是一段可复用的代码块，接受输入参数并返回结果。它帮助组织代码、减少重复。", Confidence = 0.8f },
                new() { Pattern = "what is.*class|类.*什么", Answer = "类是面向对象编程的基本单元，封装了数据(属性)和行为(方法)。它是创建对象的模板。", Confidence = 0.8f },
                new() { Pattern = "how.*debug|怎么调试|如何调试", Answer = "调试步骤：1)设置断点 2)运行调试模式 3)单步执行 4)检查变量值 5)分析调用栈。", Confidence = 0.75f },
                new() { Pattern = "what is.*api|API.*什么", Answer = "API(Application Programming Interface)是应用程序编程接口，定义了不同软件组件之间的通信规则。", Confidence = 0.8f },
            },
            ["math"] = new()
            {
                new() { Pattern = "what is.*pi|圆周率.*什么|π.*多少", Answer = "圆周率π≈3.14159265358979...，是圆的周长与直径之比，是一个无理数。", Confidence = 0.95f },
                new() { Pattern = "勾股定理|pythagorean", Answer = "勾股定理：直角三角形中，斜边的平方等于两直角边的平方和。即 a²+b²=c²。", Confidence = 0.9f },
                new() { Pattern = "what is.*derivative|导数.*什么", Answer = "导数表示函数在某一点的瞬时变化率，几何意义是切线斜率。记作 f'(x) 或 df/dx。", Confidence = 0.8f },
            },
            ["science"] = new()
            {
                new() { Pattern = "what is.*gravity|重力.*什么|引力", Answer = "重力是物体之间相互吸引的力。地球表面的重力加速度约为9.8m/s²。牛顿万有引力定律：F=G(m₁m₂)/r²。", Confidence = 0.85f },
                new() { Pattern = "what is.*atom|原子.*什么", Answer = "原子是化学元素的最小单位，由原子核(质子+中子)和电子组成。直径约0.1纳米。", Confidence = 0.85f },
                new() { Pattern = "what is.*photosynthesis|光合作用", Answer = "光合作用是植物利用光能将CO₂和H₂O转化为有机物和O₂的过程。公式：6CO₂+6H₂O→C₆H₁₂O₆+6O₂。", Confidence = 0.85f },
            },
            ["language"] = new()
            {
                new() { Pattern = "what does.*mean|.*什么意思|.*是什么意思", Answer = "请提供具体的词语或句子，我来帮你解释。", Confidence = 0.6f },
                new() { Pattern = "how.*spell|怎么拼写|拼写", Answer = "请告诉我你想拼写的单词，我来帮你。", Confidence = 0.7f },
            },
            ["system"] = new()
            {
                new() { Pattern = "how.*install|怎么安装|如何安装", Answer = "安装步骤通常是：1)下载软件 2)运行安装程序 3)按向导完成安装。具体步骤取决于软件类型。", Confidence = 0.7f },
                new() { Pattern = "what is.*config|配置.*什么|什么是配置", Answer = "配置是软件运行的参数设置，通常存储在配置文件中(如.json,.yaml,.ini)，用于自定义软件行为。", Confidence = 0.75f },
                new() { Pattern = "how.*fix.*error|怎么修复.*错误|error.*怎么办", Answer = "排查错误步骤：1)阅读错误信息 2)搜索错误代码 3)检查日志 4)复现问题 5)逐步排查。", Confidence = 0.7f },
            },
        };

        foreach (var (domain, answers) in defaults)
        {
            _answers[domain] = answers;
        }

        _logger.LogInformation("CellAnswerStore initialized with {Count} domains, {Total} answers",
            _answers.Count, _answers.Values.Sum(a => a.Count));
    }

    public CellAnswerResult FindAnswer(string domain, string query)
    {
        if (!_answers.TryGetValue(domain, out var answers))
            return new CellAnswerResult { Found = false };

        var lower = query.ToLowerInvariant();

        foreach (var answer in answers)
        {
            var patterns = answer.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pattern in patterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lower, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    lock (_lock)
                    {
                        var idx = answers.IndexOf(answer);
                        answers[idx] = answer with
                        {
                            HitCount = answer.HitCount + 1,
                            LastHit = DateTime.UtcNow
                        };
                    }

                    return new CellAnswerResult
                    {
                        Found = true,
                        Answer = answer.Answer,
                        Confidence = answer.Confidence,
                        MatchedPattern = pattern
                    };
                }
            }
        }

        return new CellAnswerResult { Found = false };
    }

    public void AddAnswer(string domain, string pattern, string answer, float confidence = 0.7f)
    {
        lock (_lock)
        {
            if (!_answers.TryGetValue(domain, out var answers))
            {
                answers = new List<CellAnswer>();
                _answers[domain] = answers;
            }

            answers.Add(new CellAnswer
            {
                Pattern = pattern,
                Answer = answer,
                Confidence = confidence
            });
        }

        _logger.LogInformation("Cell answer added: domain={Domain}, pattern={Pattern}", domain, pattern);
    }

    public void LearnFromL2(string domain, string query, string answer, float confidence)
    {
        var pattern = BuildPatternFromQuery(query);
        AddAnswer(domain, pattern, answer, confidence * 0.8f);
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["domain_count"] = _answers.Count,
                ["total_answers"] = _answers.Values.Sum(a => a.Count),
                ["by_domain"] = _answers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new { Count = kvp.Value.Count, TotalHits = kvp.Value.Sum(a => a.HitCount) })
            };
        }
    }

    private static string BuildPatternFromQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Select(w => System.Text.RegularExpressions.Regex.Replace(w.ToLowerInvariant(), @"[^\w]", ""))
            .Where(w => !string.IsNullOrEmpty(w))
            .Take(3)
            .ToList();

        return words.Count > 0 ? string.Join(".*", words) : query.ToLowerInvariant();
    }
}
