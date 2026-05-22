using System.Text.Json;

namespace LTAI.Agent.Governance;

public enum PolicySeverity { Audit, Warn, Block }
public enum AgentAction { ToolCall, FileWrite, NetworkAccess, ModelCall, CodeExecute, SkillRun, DataAccess }

public sealed class PolicyRule
{
    public string Name { get; set; } = "";
    public AgentAction Action { get; set; }
    public string Pattern { get; set; } = "";
    public PolicySeverity Severity { get; set; } = PolicySeverity.Warn;
    public string Description { get; set; } = "";
    public Func<Dictionary<string, object?>, bool>? Condition { get; set; }
}

public sealed class GovernanceDecision
{
    public bool Allowed { get; set; } = true;
    public PolicySeverity Severity { get; set; } = PolicySeverity.Audit;
    public string Rule { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}

public sealed class ActionGovernor : IDisposable
{
    private static readonly Lazy<ActionGovernor> _instance = new(() => new ActionGovernor());
    public static ActionGovernor Instance => _instance.Value;

    private readonly List<PolicyRule> _rules = new();
    private readonly List<Dictionary<string, object>> _auditTrail = new();
    private readonly string _auditPath;
    private readonly object _lock = new();
    private int _blocked, _warned, _total;

    private ActionGovernor()
    {
        _auditPath = global::System.IO.Path.Combine(".livingtree", "governance", "audit.jsonl");
        var dir = global::System.IO.Path.GetDirectoryName(_auditPath);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        LoadRules();
    }

    private void LoadRules()
    {
        _rules.AddRange(new[]
        {
            new PolicyRule { Name = "block_rm_rf", Action = AgentAction.ToolCall, Pattern = @"rm\s+-rf\s+/", Severity = PolicySeverity.Block, Description = "Block destructive shell commands" },
            new PolicyRule { Name = "block_credential_leak", Action = AgentAction.FileWrite, Pattern = @"(api_key|password|secret|token)\s*=\s*['""].+['""]", Severity = PolicySeverity.Block, Description = "Block writing credentials to files" },
            new PolicyRule { Name = "warn_large_network", Action = AgentAction.NetworkAccess, Severity = PolicySeverity.Warn, Description = "Log large outbound data transfers", Condition = args => args.TryGetValue("size_kb", out var s) && s is double d && d > 1024 },
            new PolicyRule { Name = "audit_model_call", Action = AgentAction.ModelCall, Severity = PolicySeverity.Audit, Description = "Log all model calls for cost tracking" },
            new PolicyRule { Name = "block_unsafe_code", Action = AgentAction.CodeExecute, Pattern = @"(Process\.Start|Runtime\.getRuntime|subprocess|eval|exec)\(", Severity = PolicySeverity.Block, Description = "Block unsafe code execution" },
            new PolicyRule { Name = "warn_data_access", Action = AgentAction.DataAccess, Severity = PolicySeverity.Warn, Description = "Warn on sensitive data access patterns" },
        });
    }

    public GovernanceDecision Evaluate(AgentAction action, Dictionary<string, object?>? args = null)
    {
        Interlocked.Increment(ref _total);
        args ??= new();
        var decision = new GovernanceDecision { Allowed = true };

        foreach (var rule in _rules.Where(r => r.Action == action))
        {
            var match = false;
            if (!string.IsNullOrEmpty(rule.Pattern))
                match = args.Values.Any(v => v?.ToString() is string s &&
                    System.Text.RegularExpressions.Regex.IsMatch(s, rule.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            else if (rule.Condition != null)
                match = rule.Condition(args);

            if (match || (string.IsNullOrEmpty(rule.Pattern) && rule.Condition == null && rule.Severity >= PolicySeverity.Warn))
            {
                decision.Rule = rule.Name;
                decision.Reason = rule.Description;
                decision.Warnings.Add(rule.Description);

                if (rule.Severity > decision.Severity)
                    decision.Severity = rule.Severity;

                if (rule.Severity == PolicySeverity.Block)
                {
                    decision.Allowed = false;
                    Interlocked.Increment(ref _blocked);
                }
                else if (rule.Severity == PolicySeverity.Warn)
                    Interlocked.Increment(ref _warned);
            }
        }

        Audit(action, args, decision);
        return decision;
    }

    public void AddRule(PolicyRule rule) { lock (_lock) _rules.Add(rule); }

    public GovernanceDecision EvaluateToolCall(string toolName, string input, Dictionary<string, object?>? toolArgs = null)
    {
        var args = new Dictionary<string, object?> { ["tool"] = toolName, ["input"] = input };
        if (toolArgs != null) foreach (var (k, v) in toolArgs) args[k] = v;
        return Evaluate(AgentAction.ToolCall, args);
    }

    private void Audit(AgentAction action, Dictionary<string, object?> args, GovernanceDecision decision)
    {
        var entry = new Dictionary<string, object>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["action"] = action.ToString(),
            ["args"] = args.Keys.ToList(),
            ["allowed"] = decision.Allowed,
            ["severity"] = decision.Severity.ToString(),
            ["rule"] = decision.Rule
        };
        lock (_lock)
        {
            _auditTrail.Add(entry);
            if (_auditTrail.Count > 1000) _auditTrail.RemoveRange(0, 200);
        }
        if (_auditTrail.Count % 10 == 0) FlushAudit();
    }

    private void FlushAudit()
    {
        List<Dictionary<string, object>> snapshot;
        lock (_lock) { snapshot = _auditTrail.TakeLast(10).ToList(); }
        foreach (var entry in snapshot)
            global::System.IO.File.AppendAllText(_auditPath, JsonSerializer.Serialize(entry) + "\n");
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_actions"] = _total, ["blocked"] = _blocked,
        ["warned"] = _warned, ["rules"] = _rules.Count,
        ["audit_entries"] = _auditTrail.Count,
        ["audit_path"] = _auditPath
    };

    public List<Dictionary<string, object>> QueryAudit(AgentAction? action = null, int limit = 50)
    {
        lock (_lock)
        {
            var q = _auditTrail.AsEnumerable();
            if (action.HasValue) q = q.Where(e => e["action"].ToString() == action.Value.ToString());
            return q.TakeLast(limit).ToList();
        }
    }

    public void Dispose() => FlushAudit();
}
