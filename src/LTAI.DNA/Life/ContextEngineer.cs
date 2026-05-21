using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Life;

public enum EvalStatus { Pass, Fail, SilentFail }

public sealed class ContextTest
{
    public string TestId { get; init; } = Guid.NewGuid().ToString("N");
    public string ContextName { get; init; } = "";
    public string Input { get; init; } = "";
    public string ExpectedBehavior { get; init; } = "";
    public string? ForbiddenBehavior { get; init; }
    public string? ActualOutput { get; set; }
    public EvalStatus Status { get; set; } = EvalStatus.Pass;
    public string? FailureReason { get; set; }

    public EvalStatus Run(string output)
    {
        ActualOutput = output;
        if (ForbiddenBehavior != null && output.Contains(ForbiddenBehavior, StringComparison.OrdinalIgnoreCase))
        {
            Status = EvalStatus.Fail;
            FailureReason = $"Forbidden behavior detected: {ForbiddenBehavior}";
            return EvalStatus.Fail;
        }
        if (!string.IsNullOrEmpty(ExpectedBehavior) && !output.Contains(ExpectedBehavior, StringComparison.OrdinalIgnoreCase))
        {
            Status = EvalStatus.SilentFail;
            FailureReason = $"Expected behavior not found: {ExpectedBehavior}";
            return EvalStatus.SilentFail;
        }
        Status = EvalStatus.Pass;
        return EvalStatus.Pass;
    }
}

public sealed class ContextEvalFramework
{
    private readonly ILogger<ContextEvalFramework> _logger;
    private readonly ConcurrentDictionary<string, List<ContextTest>> _tests = new();

    public ContextEvalFramework(ILogger<ContextEvalFramework>? logger = null)
    {
        _logger = logger ?? NullLogger<ContextEvalFramework>.Instance;
    }

    public void AddTest(string contextName, ContextTest test)
    {
        _tests.AddOrUpdate(contextName,
            _ => new List<ContextTest> { test },
            (_, list) => { list.Add(test); return list; });
    }

    public Dictionary<string, object> TddCycle()
    {
        var results = new Dictionary<string, object>();
        int pass = 0, fail = 0, silent = 0;
        var failures = new List<string>();

        foreach (var (context, tests) in _tests)
        {
            foreach (var test in tests)
            {
                var output = SimulateContext(test.ContextName);
                var status = test.Run(output);
                switch (status)
                {
                    case EvalStatus.Pass: pass++; break;
                    case EvalStatus.Fail: fail++; failures.Add($"{test.ContextName}: {test.FailureReason}"); break;
                    case EvalStatus.SilentFail: silent++; break;
                }
            }
        }

        results["pass"] = pass;
        results["fail"] = fail;
        results["silent_fail"] = silent;
        results["failure_reasons"] = failures;
        return results;
    }

    private static string SimulateContext(string contextName) => $"Simulated output for {contextName}";

    public Dictionary<string, object> GetStats()
    {
        int totalTests = 0;
        foreach (var (_, tests) in _tests) totalTests += tests.Count;
        return new Dictionary<string, object>
        {
            ["contexts"] = _tests.Count,
            ["total_tests"] = totalTests,
        };
    }
}

public sealed class ContextSecurityScanner
{
    private static readonly (string pattern, string threatType)[] ThreatPatterns =
    {
        (@"<script[\s>]", "XSS"),
        (@"\bDROP\s+TABLE\b", "SQL Injection"),
        (@"eval\s*\(.*\)", "Code Execution"),
        (@"os\.system\s*\(", "Shell Injection"),
        (@"__import__\s*\(", "Import Abuse"),
        (@"\.\.\/\.\.\/", "Path Traversal"),
    };

    public List<Dictionary<string, object>> Scan(string text, string source = "")
    {
        var findings = new List<Dictionary<string, object>>();
        foreach (var (pattern, threatType) in ThreatPatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
            {
                findings.Add(new Dictionary<string, object>
                {
                    ["threat_type"] = threatType,
                    ["source"] = source,
                    ["pattern"] = pattern,
                    ["severity"] = threatType switch
                    {
                        "XSS" or "SQL Injection" => "critical",
                        "Code Execution" or "Shell Injection" => "high",
                        _ => "medium",
                    },
                });
            }
        }

        if (Core.System.PromptShield.HasPromptInjectionPattern(text))
        {
            findings.Add(new Dictionary<string, object>
            {
                ["threat_type"] = "Prompt Injection",
                ["source"] = source,
                ["pattern"] = "PromptShield composite",
                ["severity"] = "critical",
            });
        }

        return findings;
    }
}

public sealed class ContextFlywheel
{
    private readonly ILogger<ContextFlywheel> _logger;
    private readonly ConcurrentDictionary<string, List<double>> _metrics = new();
    private int _cycleCount;

    public ContextFlywheel(ILogger<ContextFlywheel>? logger = null)
    {
        _logger = logger ?? NullLogger<ContextFlywheel>.Instance;
    }

    public void Record(string metric, double value)
    {
        _metrics.AddOrUpdate(metric,
            _ => new List<double> { value },
            (_, list) => { list.Add(value); if (list.Count > 100) list.RemoveAt(0); return list; });
    }

    public Dictionary<string, double> FlywheelHealth()
    {
        _cycleCount++;
        var health = new Dictionary<string, double>();
        foreach (var (metric, values) in _metrics)
        {
            if (values.Count >= 2)
            {
                var trend = values.TakeLast(5).Average() - values.Take(5).Average();
                health[metric] = Math.Round(trend, 4);
            }
        }
        return health;
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["cycles"] = _cycleCount,
            ["metrics_tracked"] = _metrics.Count,
        };
    }
}

public sealed class ContextEngineer
{
    private readonly ILogger<ContextEngineer> _logger;
    public ContextEvalFramework Eval { get; } = new();
    public ContextSecurityScanner Security { get; } = new ContextSecurityScanner();
    public ContextFlywheel Flywheel { get; } = new();

    public ContextEngineer(ILogger<ContextEngineer>? logger = null)
    {
        _logger = logger ?? NullLogger<ContextEngineer>.Instance;
    }

    public Dictionary<string, object> FullContextAudit(string contextText)
    {
        var findings = Security.Scan(contextText);
        var health = Flywheel.FlywheelHealth();

        return new Dictionary<string, object>
        {
            ["security_findings"] = findings,
            ["flywheel_health"] = health,
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
        };
    }
}
