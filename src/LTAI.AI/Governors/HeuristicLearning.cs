using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// HL (Heuristic Learning) — Learning Beyond Gradients 启发的反馈驱动改进系统。
/// 
/// 核心理念：用编程 Agent 的代码编辑替代梯度反向传播，
/// 将"学习"建模为对显式规则集的反馈吸收 + 历史压缩 + 回归保护。
/// 
/// 参考：Weng, Jiayi. "Learning Beyond Gradients" (2026)
/// </summary>

/// <summary>
/// 规则编辑操作: Add / Modify / Retract.
/// 每一次 HL 循环的反馈都产生一条 EditRecord, 系统可完整回溯。
/// </summary>
public enum EditOp { Add, Modify, Retract }

public sealed record EditRecord
{
    public string RuleId { get; init; } = "";
    public EditOp Op { get; init; }
    public string RuleContent { get; init; } = "";
    public string FeedbackSource { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Reward { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int Version { get; init; }
}

/// <summary>
/// 回归测试用例: 保护旧能力不被新规则破坏。
/// HL 论文关键洞察: "old capabilities can be fixed into regression tests".
/// </summary>
public sealed record RegressionTestCase
{
    public string Query { get; init; } = "";
    public string ExpectedAnswerContains { get; init; } = "";
    public string RuleId { get; init; } = "";
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
    public int PassCount { get; set; }
    public int FailCount { get; set; }
}

public sealed record RegressionReport
{
    public int TotalTests { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public List<RegressionTestCase> Failures { get; init; } = new();
    public double PassRate => TotalTests > 0 ? (double)Passed / TotalTests : 1.0;
}

/// <summary>
/// Heuristic Registry — 显式、可读、可逆的规则寄存器。
/// 
/// 替代 SelfEvolutionLoop 中的隐式突变,
/// 每次规则变更都产生一条永久可查的 EditRecord。
/// HL 论文: "HL history is explicit, readable, deletable, and refactorable."
/// </summary>
public sealed class HeuristicRegistry
{
    private readonly ConcurrentDictionary<string, string> _rules = new();
    private readonly List<EditRecord> _history = new();
    private readonly List<RegressionTestCase> _regressionTests = new();
    private readonly ILogger<HeuristicRegistry> _logger;
    private int _version;

    public HeuristicRegistry(ILogger<HeuristicRegistry>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HeuristicRegistry>.Instance;
    }

    public IReadOnlyList<EditRecord> History => _history.AsReadOnly();
    public IReadOnlyList<RegressionTestCase> RegressionTests => _regressionTests.AsReadOnly();
    public int Version => _version;

    public EditRecord AddRule(string id, string content, string source, string reason)
    {
        var record = new EditRecord
        {
            RuleId = id, Op = EditOp.Add, RuleContent = content,
            FeedbackSource = source, Reason = reason, Version = ++_version
        };
        _rules[id] = content;
        _history.Add(record);
        _logger.LogInformation("HL: +{RuleId} from {Source} ({Reason})", id, source, reason);
        return record;
    }

    public EditRecord ModifyRule(string id, string content, string source, string reason)
    {
        var record = new EditRecord
        {
            RuleId = id, Op = EditOp.Modify, RuleContent = content,
            FeedbackSource = source, Reason = reason, Version = ++_version
        };
        _rules[id] = content;
        _history.Add(record);
        _logger.LogInformation("HL: ~{RuleId} from {Source}", id, source);
        return record;
    }

    public EditRecord RetractRule(string id, string source, string reason)
    {
        var record = new EditRecord
        {
            RuleId = id, Op = EditOp.Retract, RuleContent = _rules.GetValueOrDefault(id, ""),
            FeedbackSource = source, Reason = reason, Version = ++_version
        };
        _rules.TryRemove(id, out _);
        _history.Add(record);
        _logger.LogInformation("HL: -{RuleId} from {Source} ({Reason})", id, source, reason);
        return record;
    }

    public string? GetRule(string id) => _rules.GetValueOrDefault(id);

    public IReadOnlyDictionary<string, string> GetAllRules()
        => new Dictionary<string, string>(_rules).AsReadOnly();

    public void AddRegressionTest(string query, string expectedContains, string ruleId)
    {
        _regressionTests.Add(new RegressionTestCase
        {
            Query = query, ExpectedAnswerContains = expectedContains, RuleId = ruleId
        });
        _logger.LogInformation("HL: regression test added for rule {RuleId}: '{Query}'", ruleId, query[..Math.Min(query.Length, 60)]);
    }

    public RegressionReport RunRegressionTests(Func<string, string> answerFunc)
    {
        var passed = 0;
        var failed = 0;
        var failures = new List<RegressionTestCase>();

        foreach (var test in _regressionTests)
        {
            try
            {
                var answer = answerFunc(test.Query);
                if (answer.Contains(test.ExpectedAnswerContains, StringComparison.OrdinalIgnoreCase))
                {
                    test.PassCount++;
                    passed++;
                }
                else
                {
                    test.FailCount++;
                    failed++;
                    failures.Add(test);
                    _logger.LogWarning("HL: regression FAIL for {RuleId}: '{Query}'",
                        test.RuleId, test.Query[..Math.Min(test.Query.Length, 60)]);
                }
            }
            catch (Exception ex)
            {
                test.FailCount++;
                failed++;
                failures.Add(test);
                _logger.LogWarning(ex, "HL: regression ERROR for {RuleId}", test.RuleId);
            }
        }

        return new RegressionReport { TotalTests = _regressionTests.Count, Passed = passed, Failed = failed, Failures = failures };
    }

    public string CompressHistory(int keepRecent = 20)
    {
        if (_history.Count <= keepRecent) return "";

        var recent = _history.TakeLast(keepRecent).ToList();
        var old = _history.Take(_history.Count - keepRecent).ToList();

        var ops = new { added = old.Count(r => r.Op == EditOp.Add),
            modified = old.Count(r => r.Op == EditOp.Modify),
            retracted = old.Count(r => r.Op == EditOp.Retract) };

        _history.Clear();
        _history.AddRange(recent);

        var summary = $"HL compressed {old.Count} old edits into {keepRecent} recent. " +
            $"Old ops: +{ops.added} ~{ops.modified} -{ops.retracted}. " +
            $"Active rules: {_rules.Count}. Regression tests: {_regressionTests.Count}.";

        _logger.LogInformation(summary);
        return summary;
    }

    public string ExportHistoryJson()
    {
        var data = new { version = _version, rules = _rules.ToDictionary(), history = _history.TakeLast(100), regressionTests = _regressionTests };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// HL Feedback Cycle — 替代 SelfEvolutionLoop 的规则突变,
/// 实现 HL 风格的 "吸收反馈 + 回归测试 + 历史压缩" 循环。
/// 
/// HL 论文: "A healthy HS needs at least two operations:
///  1. Absorb feedback: write failures, logs, rewards into the system.
///  2. Compress history: fold local patches into simpler representations."
/// </summary>
public sealed class HLFeedbackCycle
{
    private readonly HeuristicRegistry _registry;
    private readonly ILogger<HLFeedbackCycle> _logger;
    private int _cycleCount;

    public HLFeedbackCycle(HeuristicRegistry? registry = null, ILogger<HLFeedbackCycle>? logger = null)
    {
        _registry = registry ?? new HeuristicRegistry();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HLFeedbackCycle>.Instance;
    }

    public HeuristicRegistry Registry => _registry;

    public EditRecord AbsorbFeedback(
        string ruleId, string content, string source,
        double reward, string reason)
    {
        var existing = _registry.GetRule(ruleId);

        if (existing == null)
            return _registry.AddRule(ruleId, content, source, reason);

        if (reward < -0.5)
            return _registry.RetractRule(ruleId, source, reason);

        return _registry.ModifyRule(ruleId, content, source, reason);
    }

    public (int compressed, string summary) CompressIfNeeded(int maxHistory = 50)
    {
        _cycleCount++;
        if (_cycleCount % 10 != 0) return (0, "");

        var summary = _registry.CompressHistory(maxHistory);
        return (1, summary);
    }

    public RegressionReport RunRegressionTests(Func<string, string> answerFunc)
    {
        _logger.LogInformation("HL: running {Count} regression tests after {CycleCount} cycles",
            _registry.RegressionTests.Count, _cycleCount);

        var report = _registry.RunRegressionTests(answerFunc);

        if (report.Failed > 0)
            _logger.LogWarning("HL: {Failed}/{Total} regression tests failed. PassRate={Rate:F2}",
                report.Failed, report.TotalTests, report.PassRate);

        return report;
    }

    public int CycleCount => _cycleCount;
}

/// <summary>
/// HL Compressor — 语义压缩: 将多条相似规则折叠为简洁表示。
/// 
/// 替代 TerminalCompressor 的 token 截断,
/// 提供语义级别的"fold local patches into simpler representations"。
/// </summary>
public sealed class HLCompressor
{
    private readonly ILogger<HLCompressor> _logger;
    private int _compressCount;

    public HLCompressor(ILogger<HLCompressor>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HLCompressor>.Instance;
    }

    public string CompressRules(IReadOnlyDictionary<string, string> rules)
    {
        _compressCount++;

        var groups = rules
            .GroupBy(kv => kv.Key[..Math.Min(kv.Key.Length, 2)])
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
            return $"HL: {rules.Count} rules, no groups to compress.";

        var compressed = new List<string>();
        foreach (var group in groups)
        {
            var merged = string.Join(" | ", group.Select(kv =>
                kv.Value.Length > 80 ? kv.Value[..80] + "..." : kv.Value));
            compressed.Add($"[{group.Key}] {group.Count()} rules: {merged}");
        }

        var summary = $"HL compressed {rules.Count} rules into {compressed.Count} groups. " +
            $"Compression #{_compressCount}.";
        _logger.LogInformation(summary);

        return summary;
    }

    public static double CompressRatio(int originalCount, int compressedCount)
        => compressedCount > 0 ? (double)compressedCount / originalCount : 1.0;
}
