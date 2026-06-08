using System.Text.Json;
using LTAI.Core.Safety;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Learning;

public sealed record FailureRecord
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Model { get; init; } = "";
    public DateTime Timestamp { get; init; }
}

public sealed record MinedRule
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Pattern { get; init; } = "";
    public int Frequency { get; init; }
}

public static class FailureRecorder
{
    private static readonly string _failuresDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".livingtree", "failures");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static long _totalFailures;

    static FailureRecorder()
    {
        Directory.CreateDirectory(_failuresDir);
    }

    public static void Record(string query, string response, string reason, string model)
    {
        try
        {
            // F10: Redact PII before persisting to disk
            var redactedQuery = SafetyRules.RedactPII(query);
            var redactedResponse = SafetyRules.RedactPII(response);

            // Skip recording entirely if content is unsafe (API keys, secrets, etc.)
            if (!SafetyRules.IsSafeByRules(redactedQuery) || !SafetyRules.IsSafeByRules(redactedResponse))
            {

                return;
            }

            var record = new FailureRecord
            {
                Query = redactedQuery,
                Response = redactedResponse,
                Reason = reason,
                Model = model,
                Timestamp = DateTime.UtcNow
            };
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(_failuresDir, $"failure_{timestamp}_{suffix}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, _jsonOpts));
            Interlocked.Increment(ref _totalFailures);
        }
        catch (Exception) { }
    }

    public static long TotalFailures => _totalFailures;

    public static List<FailureRecord> LoadAll()
    {
        var results = new List<FailureRecord>();
        if (!Directory.Exists(_failuresDir)) return results;

        foreach (var file in Directory.GetFiles(_failuresDir, "failure_*.json")
                     .OrderByDescending(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<FailureRecord>(json);
                if (record != null) results.Add(record);
            }
            catch { }
        }
        return results;
    }
}

public sealed class FailureMiner
{
    private readonly ILogger<FailureMiner> _logger;
    private static readonly string _rulesLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".livingtree", "learn");
    private static readonly string _rulesLogPath = Path.Combine(_rulesLogDir, "rules.log");

    public FailureMiner(ILogger<FailureMiner>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FailureMiner>.Instance;
        Directory.CreateDirectory(_rulesLogDir);
    }

    public List<MinedRule> Mine()
    {
        var failures = FailureRecorder.LoadAll();
        if (failures.Count == 0)
        {
            _logger.LogInformation("FailureMiner: no failures to mine");
            return [];
        }

        _logger.LogInformation("FailureMiner: mining {Count} failures", failures.Count);

        var clusters = ClusterByReason(failures);
        var rules = new List<MinedRule>();

        foreach (var (reason, records) in clusters.OrderByDescending(c => c.Value.Count))
        {
            if (records.Count < 2) continue;
            rules.Add(GenerateRule(reason, records));
        }

        return rules;
    }

    public int AppendRulesToLog(List<MinedRule> rules)
    {
        if (rules.Count == 0) return 0;

        var count = 0;
        try
        {
            foreach (var rule in rules)
            {
                var entry = JsonSerializer.Serialize(new
                {
                    rule.Title,
                    rule.Description,
                    rule.Pattern,
                    rule.Frequency,
                    timestamp = DateTime.UtcNow
                });
                File.AppendAllText(_rulesLogPath, entry + "\n");
                count++;
            }
            _logger.LogInformation("FailureMiner: appended {Count} rules to {Path}", count, _rulesLogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailureMiner: failed to append rules");
        }
        return count;
    }

    private static Dictionary<string, List<FailureRecord>> ClusterByReason(List<FailureRecord> failures)
    {
        var clusters = new Dictionary<string, List<FailureRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in failures)
        {
            var key = NormalizeReason(f.Reason);
            if (!clusters.ContainsKey(key))
                clusters[key] = [];
            clusters[key].Add(f);
        }

        return clusters;
    }

    private static string NormalizeReason(string reason)
    {
        if (reason.Contains("编造") || reason.Contains("hallucinat") || reason.Contains("不真实"))
            return "hallucination";
        if (reason.Contains("拒绝") || reason.Contains("refus") || reason.Contains("无法"))
            return "refusal";
        if (reason.Contains("空泛") || reason.Contains("vague") || reason.Contains("空泛"))
            return "vague";
        return reason;
    }

    private static MinedRule GenerateRule(string reason, List<FailureRecord> records)
    {
        var (title, description) = reason switch
        {
            "hallucination" => (
                "禁止编造数据",
                "AI必须基于工具调用结果回答，不得编造数据。如果需要实时/本地信息（文件内容、系统状态、网络数据），" +
                "必须先调用对应工具。如果问题需要但工具列表中没有对应工具，必须明确告知用户缺少的能力。"),
            "refusal" => (
                "禁止直接拒绝",
                "AI不应直接拒绝回答。即使不确定，也应给出已知的相关信息或建议替代方案。如问题涉及敏感信息，" +
                "提示用户可查看隐私政策或使用本地工具自行检查。"),
            "vague" => (
                "回答必须具体",
                "AI回答必须具体：包含数字、事实、代码示例、文件路径等可验证的内容。避免空泛的结论性描述。"),
            _ => (
                $"失败模式: {reason}",
                $"自动检测到的失败模式：{records[0].Reason}。请人工审查后补充规则。")
        };

        return new MinedRule
        {
            Title = title,
            Description = description,
            Pattern = records[0].Reason,
            Frequency = records.Count
        };
    }
}
