using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Economy;

public sealed class ROIModel
{
    private static readonly Dictionary<string, double> TaskValueBase = new()
    {
        ["code_generation"] = 3.0, ["code_review"] = 2.0, ["document_generation"] = 2.5,
        ["data_analysis"] = 2.0, ["environmental_report"] = 5.0, ["bug_fix"] = 2.5,
        ["research"] = 1.5, ["question"] = 0.5, ["chat"] = 0.2, ["general"] = 1.0
    };

    private readonly Dictionary<string, double> _modelPriceInput;
    private readonly Dictionary<string, double> _modelPriceOutput;

    private static readonly double TokenBaseMs = 500;

    private double _cumulativeCost;
    private double _cumulativeValue;
    private int _evaluationCount;

    public ROIModel(IOptions<LTAIOptions>? options = null)
    {
        var pricing = options?.Value.ModelPricing;
        _modelPriceInput = pricing != null
            ? new Dictionary<string, double>(pricing.InputPer1M, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _modelPriceOutput = pricing != null
            ? new Dictionary<string, double>(pricing.OutputPer1M, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    public double EstimateValue(string taskType, double complexity = 0.5,
        double userPriority = 0.5, string taskDesc = "")
    {
        TaskValueBase.TryGetValue(taskType, out var baseValue);
        if (baseValue == 0) baseValue = 1.0;

        var value = baseValue * (0.5 + complexity) * (0.5 + userPriority);

        if (!string.IsNullOrEmpty(taskDesc))
        {
            var lower = taskDesc.ToLowerInvariant();
            if (lower.Contains("env") || lower.Contains("environment") || lower.Contains("legal"))
                value += 2.0;
            if (lower.Contains("urgent") || lower.Contains("urgently") || lower.Contains("immediately"))
                value += 1.0;
            if (lower.Contains("security") || lower.Contains("secure") || lower.Contains("vulnerability"))
                value += 1.5;
        }

        return Math.Max(0, value);
    }

    public double EstimateCost(int estimatedTokens, string model)
    {
        _modelPriceInput.TryGetValue(model, out var inputPrice);
        _modelPriceOutput.TryGetValue(model, out var outputPrice);
        var avgPrice = (inputPrice + outputPrice) / 2.0;
        return avgPrice * estimatedTokens / 1_000_000.0;
    }

    public ROIResult Evaluate(string taskId, string taskType, int estimatedTokens,
        string model, double complexity = 0.5, double userPriority = 0.5,
        double predictedQuality = 0.5, EconomicPolicy? policy = null, string taskDesc = "")
    {
        var value = EstimateValue(taskType, complexity, userPriority, taskDesc);
        var cost = EstimateCost(estimatedTokens, model);
        var estimatedMs = TokenBaseMs + estimatedTokens * 30.0;

        var budgetYuan = policy?.MaxTaskBudgetYuan ?? 10.0;
        var timeoutMs = 120000.0;
        var trilemma = TrilemmaVector.FromRaw(cost, estimatedMs, predictedQuality, budgetYuan, timeoutMs);

        var roi = value / Math.Max(cost, 0.0001);
        var score = trilemma.WeightedScore(policy);
        var threshold = policy?.RoiThreshold ?? 0.5;
        var minScore = policy?.MinScore ?? 0.3;
        var minQuality = policy?.MinQualityThreshold ?? 0.4;
        var maxBudget = policy?.MaxTaskBudgetYuan ?? 10.0;

        var approved = roi >= threshold
                       && score >= minScore
                       && predictedQuality >= minQuality
                       && cost <= maxBudget;

        return new ROIResult
        {
            TaskId = taskId,
            TaskValue = value,
            EstimatedCostYuan = cost,
            RoiEstimate = roi,
            Trilemma = trilemma,
            Score = score,
            Approved = approved,
            Reason = approved ? "ROI threshold met" : "ROI below threshold"
        };
    }

    public void RecordActual(ROIResult result, double actualCostYuan)
    {
        result.ActualCostYuan = actualCostYuan;
        result.RoiActual = result.TaskValue / Math.Max(actualCostYuan, 0.0001);
        _cumulativeCost += actualCostYuan;
        _cumulativeValue += result.TaskValue;
        _evaluationCount++;
    }

    public double CumulativeROI() => _cumulativeValue / Math.Max(_cumulativeCost, 0.0001);

    public IReadOnlyDictionary<string, object> Stats() => new Dictionary<string, object>
    {
        ["cumulative_cost"] = _cumulativeCost,
        ["cumulative_value"] = _cumulativeValue,
        ["cumulative_roi"] = CumulativeROI(),
        ["evaluation_count"] = _evaluationCount
    };
}

public sealed class ComplianceGate
{
    private static readonly (string Name, Regex Pattern)[] SensitivePatterns =
    [
        ("身份证", new Regex(@"\b[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("手机号", new Regex(@"\b1[3-9]\d{9}\b", RegexOptions.Compiled)),
        ("银行卡", new Regex(@"\b\d{16,19}\b", RegexOptions.Compiled)),
        ("密码", new Regex(@"(?:密码|password|passwd)\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("API密钥", new Regex(@"(?:api[_-]?key|apikey|secret[_-]?key)\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase))
    ];

    private static readonly string[] EnvRedlines =
    [
        "伪造数据", "篡改报告", "隐瞒排放", "违规审批",
        "伪造监测", "编制虚假", "虚报", "瞒报"
    ];

    private static readonly (string Name, Regex Pattern)[] DangerousCode =
    [
        ("sql_drop", new Regex(@"DROP\s+TABLE", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("disk_format", new Regex(@"\b(format|mkfs)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("rm_root", new Regex(@"rm\s+-rf\s+/etc", RegexOptions.Compiled)),
        ("chmod_777", new Regex(@"chmod\s+777", RegexOptions.Compiled)),
        ("reverse_shell", new Regex(@"(bash|nc|netcat|ncat)\s+.*\b(>&|/dev/tcp)", RegexOptions.Compiled | RegexOptions.IgnoreCase))
    ];

    private readonly ComplianceLevel _level;
    private int _taskCount;
    private int _blockedCount;

    public ComplianceGate(ComplianceLevel level = ComplianceLevel.Normal)
    {
        _level = level;
    }

    public ComplianceResult CheckTask(string taskDesc, string taskType,
        IReadOnlyList<string>? codeSnippets = null, string userContext = "")
    {
        if (_level == ComplianceLevel.Permissive)
        {
            _taskCount++;
            return new ComplianceResult { Passed = true, RiskLevel = "low", Notes = "permissive mode" };
        }

        _taskCount++;
        var violations = new List<string>();
        var checks = new List<string>();

        foreach (var (name, pattern) in SensitivePatterns)
        {
            if (pattern.IsMatch(taskDesc))
            {
                violations.Add($"sensitive_{name}");
                checks.Add($"sensitive_{name}");
            }
            if (!string.IsNullOrEmpty(userContext) && pattern.IsMatch(userContext))
            {
                violations.Add($"sensitive_{name}_context");
                checks.Add($"sensitive_{name}_context");
            }
        }

        foreach (var redline in EnvRedlines)
        {
            if (taskDesc.Contains(redline, StringComparison.Ordinal))
            {
                violations.Add($"env_{redline}");
                checks.Add($"env_{redline}");
            }
        }

        if (codeSnippets != null)
        {
            foreach (var (name, pattern) in DangerousCode)
            {
                foreach (var snippet in codeSnippets)
                {
                    if (snippet != null && pattern.IsMatch(snippet))
                    {
                        violations.Add($"dangerous_{name}");
                        checks.Add($"dangerous_{name}");
                    }
                }
            }
        }

        var riskLevel = violations.Count switch
        {
            0 => "low",
            1 => "medium",
            _ => "high"
        };

        var requiresApproval = _level == ComplianceLevel.Strict && violations.Count > 0;
        var passed = _level == ComplianceLevel.Strict
            ? violations.Count == 0
            : riskLevel != "high";

        if (!passed) _blockedCount++;

        return new ComplianceResult
        {
            Passed = passed,
            Checks = checks,
            Violations = violations,
            RiskLevel = riskLevel,
            RequiresApproval = requiresApproval,
            Notes = passed ? "ok" : $"blocked: {violations.Count} violations"
        };
    }

    public IReadOnlyDictionary<string, object> Stats() => new Dictionary<string, object>
    {
        ["level"] = _level.ToString(),
        ["task_count"] = _taskCount,
        ["blocked_count"] = _blockedCount
    };
}

public sealed class EconomicOrchestrator
{
    private static readonly HashSet<string> QualityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "code_generation", "code_review", "bug_fix", "research", "document_generation"
    };

    private static readonly HashSet<string> SpeedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat", "question", "general"
    };

    private static readonly HashSet<string> EconomyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "environmental_report", "data_analysis"
    };

    private readonly ILogger<EconomicOrchestrator> _logger;
    private readonly Lazy<ROIModel> _roiModel = new(() => new ROIModel());
    private readonly Lazy<ComplianceGate> _complianceGate;
    private readonly Lazy<ThermodynamicBudget> _thermoBudget = new(() => new ThermodynamicBudget());

    private double _dailySpentYuan;
    private readonly Dictionary<string, double> _sessionBudgets = new();

    public EconomicOrchestrator(ILogger<EconomicOrchestrator>? logger = null, IOptions<LTAIOptions>? options = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _roiModel = new Lazy<ROIModel>(() => new ROIModel(options));
        _complianceGate = new Lazy<ComplianceGate>(() => new ComplianceGate(ComplianceLevel.Normal));
    }

    public ROIModel RoiModel => _roiModel.Value;
    public ComplianceGate Compliance => _complianceGate.Value;
    public ThermodynamicBudget ThermoBudget => _thermoBudget.Value;

    public EconomicPolicy SelectPolicy(string taskType, double userPriority = 0.5)
    {
        if (QualityTypes.Contains(taskType)) return EconomicPolicy.Quality();
        if (SpeedTypes.Contains(taskType)) return EconomicPolicy.Speed();
        if (EconomyTypes.Contains(taskType)) return EconomicPolicy.Economy();
        return EconomicPolicy.Balanced();
    }

    public EconomicDecision Evaluate(string taskId, string taskDesc, string taskType,
        int estimatedTokens, double complexity = 0.5, double userPriority = 0.5,
        double predictedQuality = 0.5, IReadOnlyList<string>? codeSnippets = null,
        string userContext = "", double dailySpentYuan = 0)
    {
        _dailySpentYuan = dailySpentYuan;

        if (complexity < 0.2 && estimatedTokens < 1000)
        {
            _logger.LogInformation("Bypass: trivial task {TaskId}", taskId);
            return new EconomicDecision
            {
                TaskId = taskId,
                TaskDesc = taskDesc,
                Go = true,
                SelectedModel = "deepseek-v4-flash",
                EstimatedTokens = estimatedTokens,
                EstimatedCostYuan = RoiModel.EstimateCost(estimatedTokens, "deepseek-v4-flash"),
                Suggestion = "bypass: trivial task",
                DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        var policy = SelectPolicy(taskType, userPriority);
        var compliance = Compliance.CheckTask(taskDesc, taskType, codeSnippets, userContext);

        if (_dailySpentYuan >= policy.MaxDailyBudgetYuan)
        {
            _logger.LogWarning("Daily budget exceeded for {TaskId}: spent {Spent} >= {Max}",
                taskId, _dailySpentYuan, policy.MaxDailyBudgetYuan);
            return new EconomicDecision
            {
                TaskId = taskId,
                TaskDesc = taskDesc,
                Go = false,
                Policy = policy,
                SelectedModel = "deepseek-v4-flash",
                Compliance = compliance,
                EstimatedTokens = estimatedTokens,
                EstimatedCostYuan = RoiModel.EstimateCost(estimatedTokens, "deepseek-v4-flash"),
                Suggestion = "daily budget exceeded",
                DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        var selectedModel = "deepseek-v4-pro";

        if (ThermoBudget.KLBudget > 0.5
            && selectedModel.Contains("flash", StringComparison.OrdinalIgnoreCase)
            && complexity > 0.5)
        {
            if (ThermoBudget.ConsumeKlBudget(0.3))
            {
                selectedModel = "deepseek-v4-pro";
                _logger.LogInformation("KL upgrade: {TaskId} flash→pro (spent 0.3KL)", taskId);
            }
        }
        else if (ThermoBudget.KLBudget < 0.1
                 && selectedModel.Contains("pro", StringComparison.OrdinalIgnoreCase)
                 && complexity < 0.6)
        {
            selectedModel = "deepseek-v4-flash";
            ThermoBudget.ContributeKlBudget(0.1);
            _logger.LogInformation("KL downgrade: {TaskId} pro→flash (earned 0.1KL)", taskId);
        }

        var roi = RoiModel.Evaluate(taskId, taskType, estimatedTokens, selectedModel,
            complexity, userPriority, predictedQuality, policy, taskDesc);

        var go = roi.Approved && compliance.Passed;

        if (!go && compliance.Passed && policy.DegradationEnabled)
        {
            _logger.LogInformation("Degradation retry: {TaskId} downgrading to flash", taskId);
            selectedModel = "deepseek-v4-flash";
            roi = RoiModel.Evaluate(taskId, taskType, estimatedTokens, selectedModel,
                complexity, userPriority, predictedQuality * 0.9, policy, taskDesc);
            go = roi.Approved && compliance.Passed;
        }

        var estimatedMs = 500 + estimatedTokens * 30.0;

        return new EconomicDecision
        {
            TaskId = taskId,
            TaskDesc = taskDesc,
            Go = go,
            Policy = policy,
            SelectedModel = selectedModel,
            Trilemma = roi.Trilemma,
            Roi = roi,
            Compliance = compliance,
            EstimatedTokens = estimatedTokens,
            EstimatedCostYuan = roi.EstimatedCostYuan,
            EstimatedMs = estimatedMs,
            Suggestion = go ? "go" : $"nogo: roi={roi.Approved}, compliance={compliance.Passed}",
            DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public void RecordActual(EconomicDecision decision, double actualCostYuan)
    {
        if (decision.Roi != null)
            RoiModel.RecordActual(decision.Roi, actualCostYuan);

        _dailySpentYuan += actualCostYuan;
        ThermoBudget.RecordSpending(actualCostYuan);

        _logger.LogInformation("Recorded actual cost: {TaskId} cost=¥{Cost}, daily=¥{Daily}",
            decision.TaskId, actualCostYuan, _dailySpentYuan);
    }

    public (double Spent, double Remaining, bool Exceeded) CheckDailyBudget(EconomicPolicy? policy = null)
    {
        var max = policy?.MaxDailyBudgetYuan ?? 50.0;
        var remaining = Math.Max(0, max - _dailySpentYuan);
        return (_dailySpentYuan, remaining, _dailySpentYuan >= max);
    }

    public double SessionBudgetRemaining(string sessionId, double allocatedYuan)
    {
        if (!_sessionBudgets.TryGetValue(sessionId, out var spent))
            spent = 0;

        return Math.Max(0, allocatedYuan - spent);
    }

    public void AllocateSessionBudget(string sessionId, double yuan)
    {
        _sessionBudgets[sessionId] = yuan;
    }

    public void RecordSessionSpend(string sessionId, double yuan)
    {
        if (!_sessionBudgets.ContainsKey(sessionId))
            _sessionBudgets[sessionId] = 0;
        _sessionBudgets[sessionId] += yuan;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var (spent, remaining, exceeded) = CheckDailyBudget();
        return new Dictionary<string, object>
        {
            ["daily_spent_yuan"] = spent,
            ["daily_remaining_yuan"] = remaining,
            ["daily_exceeded"] = exceeded,
            ["roi"] = RoiModel.Stats(),
            ["compliance"] = Compliance.Stats(),
            ["thermo"] = ThermoBudget.Stats()
        };
    }

    private sealed class NullLogger : ILogger<EconomicOrchestrator>
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

public static class AdaptiveEconomicScheduler
{
    public static EconomicOrchestrator Orchestrator { get; } = new();

    public static EconomicPolicy SelectPolicy(double userPriority, ROIModel? roiModel = null)
    {
        if (userPriority > 0.8)
            return EconomicPolicy.Quality();

        var now = DateTime.Now;
        var isWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var isNight = now.Hour >= 22 || now.Hour < 7;

        if (isWeekend || isNight)
            return EconomicPolicy.Economy();

        var policy = EconomicPolicy.Balanced();

        if (roiModel != null)
        {
            var cumulativeRoi = roiModel.CumulativeROI();

            if (cumulativeRoi > 5.0)
            {
                policy.MaxDailyBudgetYuan *= 1.5;
                policy.MaxTaskBudgetYuan *= 1.5;
                policy.RoiThreshold *= 0.7;
            }
            else if (cumulativeRoi < 1.0)
            {
                policy.MaxDailyBudgetYuan *= 0.7;
                policy.MaxTaskBudgetYuan *= 0.7;
                policy.RoiThreshold *= 1.3;
            }
        }

        return policy;
    }
}
