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

// ==================== 分层计划存储 (Generative Agents 论文机制) ====================

public enum PlanLevel { Daily, Hourly, Immediate }

public record CellPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string CellId { get; init; } = "";
    public PlanLevel Level { get; init; }
    public string Content { get; init; } = "";  // 计划内容
    public string Domain { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; init; }
    public DateTime? InvalidatedAt { get; init; }  // 计划失效时间
    public bool IsValid => !InvalidatedAt.HasValue && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow);
    public int ExecutionCount { get; init; }
    public float SuccessRate { get; init; }
    public List<string> SubPlans { get; init; } = new();  // 子计划 ID
    public string ParentPlanId { get; init; } = "";
}

public record PlanInvalidationEvent
{
    public string CellId { get; init; } = "";
    public string Reason { get; init; } = "";
    public List<string> AffectedPlanIds { get; init; } = new();
    public DateTime TriggeredAt { get; init; } = DateTime.UtcNow;
    public int Priority { get; init; }  // 高优先级事件立即触发重规划
}

public record PlanQueryResult
{
    public List<CellPlan> DailyPlans { get; init; } = new();
    public List<CellPlan> HourlyPlans { get; init; } = new();
    public List<CellPlan> ImmediatePlans { get; init; } = new();
    public CellPlan? ActivePlan { get; init; }
}

public sealed class CellAnswerStore
{
    private readonly Dictionary<string, List<CellAnswer>> _answers = new();
    private readonly Dictionary<string, List<CellPlan>> _plans = new();  // CellId -> Plans
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

    // ==================== 分层计划管理 ====================

    /// <summary>
    /// 创建分层计划 (日→时→瞬间)
    /// </summary>
    public CellPlan CreatePlan(string cellId, PlanLevel level, string content, string domain, TimeSpan? validity = null)
    {
        var plan = new CellPlan
        {
            CellId = cellId,
            Level = level,
            Content = content,
            Domain = domain,
            ExpiresAt = validity.HasValue ? DateTime.UtcNow.Add(validity.Value) : null
        };

        lock (_lock)
        {
            if (!_plans.TryGetValue(cellId, out var plans))
            {
                plans = new List<CellPlan>();
                _plans[cellId] = plans;
            }
            plans.Add(plan);
        }

        _logger.LogInformation(
            "Plan created: cell={Cell} level={Level} domain={Domain}",
            cellId, level, domain);

        return plan;
    }

    /// <summary>
    /// 查询 Cell 的分层计划
    /// </summary>
    public PlanQueryResult QueryPlans(string cellId)
    {
        lock (_lock)
        {
            if (!_plans.TryGetValue(cellId, out var plans))
            {
                return new PlanQueryResult();
            }

            var validPlans = plans.Where(p => p.IsValid).ToList();

            return new PlanQueryResult
            {
                DailyPlans = validPlans.Where(p => p.Level == PlanLevel.Daily).OrderByDescending(p => p.CreatedAt).ToList(),
                HourlyPlans = validPlans.Where(p => p.Level == PlanLevel.Hourly).OrderByDescending(p => p.CreatedAt).ToList(),
                ImmediatePlans = validPlans.Where(p => p.Level == PlanLevel.Immediate).OrderByDescending(p => p.CreatedAt).ToList(),
                ActivePlan = validPlans
                    .Where(p => p.Level == PlanLevel.Immediate)
                    .OrderByDescending(p => p.CreatedAt)
                    .FirstOrDefault()
            };
        }
    }

    /// <summary>
    /// 标记计划失效 (用于事件驱动重规划)
    /// </summary>
    public PlanInvalidationEvent InvalidatePlans(string cellId, string reason, int priority = 0)
    {
        var affectedIds = new List<string>();

        lock (_lock)
        {
            if (!_plans.TryGetValue(cellId, out var plans))
            {
                return new PlanInvalidationEvent { CellId = cellId, Reason = reason };
            }

            var now = DateTime.UtcNow;

            // 高优先级事件使所有计划失效
            if (priority >= 5)
            {
                foreach (var plan in plans.Where(p => p.IsValid))
                {
                    affectedIds.Add(plan.Id);
                    // 使用反射更新不可变记录
                    var idx = plans.IndexOf(plan);
                    plans[idx] = plan with { InvalidatedAt = now };
                }
            }
            else
            {
                // 低优先级事件仅使即时计划失效
                foreach (var plan in plans.Where(p => p.IsValid && p.Level == PlanLevel.Immediate))
                {
                    affectedIds.Add(plan.Id);
                    var idx = plans.IndexOf(plan);
                    plans[idx] = plan with { InvalidatedAt = now };
                }
            }
        }

        var evt = new PlanInvalidationEvent
        {
            CellId = cellId,
            Reason = reason,
            AffectedPlanIds = affectedIds,
            Priority = priority
        };

        _logger.LogInformation(
            "Plans invalidated: cell={Cell} reason={Reason} count={Count} priority={Priority}",
            cellId, reason, affectedIds.Count, priority);

        return evt;
    }

    /// <summary>
    /// 记录计划执行反馈
    /// </summary>
    public void RecordPlanExecution(string planId, bool wasSuccessful)
    {
        lock (_lock)
        {
            foreach (var plans in _plans.Values)
            {
                var planIdx = plans.FindIndex(p => p.Id == planId);
                if (planIdx >= 0)
                {
                    var plan = plans[planIdx];
                    var totalExecutions = plan.ExecutionCount + 1;
                    var newSuccessRate = (plan.SuccessRate * plan.ExecutionCount + (wasSuccessful ? 1.0f : 0.0f)) / totalExecutions;

                    plans[planIdx] = plan with
                    {
                        ExecutionCount = totalExecutions,
                        SuccessRate = newSuccessRate
                    };

                    _logger.LogDebug(
                        "Plan execution recorded: id={Id} success={Success} rate={Rate:F2}",
                        planId, wasSuccessful, newSuccessRate);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 获取计划统计
    /// </summary>
    public Dictionary<string, object> GetPlanStats()
    {
        lock (_lock)
        {
            var allPlans = _plans.Values.SelectMany(p => p).ToList();
            var validPlans = allPlans.Where(p => p.IsValid).ToList();

            return new Dictionary<string, object>
            {
                ["total_plans"] = allPlans.Count,
                ["valid_plans"] = validPlans.Count,
                ["invalidated_plans"] = allPlans.Count - validPlans.Count,
                ["by_level"] = new Dictionary<string, int>
                {
                    ["daily"] = validPlans.Count(p => p.Level == PlanLevel.Daily),
                    ["hourly"] = validPlans.Count(p => p.Level == PlanLevel.Hourly),
                    ["immediate"] = validPlans.Count(p => p.Level == PlanLevel.Immediate)
                },
                ["average_success_rate"] = validPlans.Count > 0 ? validPlans.Average(p => (double)p.SuccessRate) : 0.0
            };
        }
    }
}
