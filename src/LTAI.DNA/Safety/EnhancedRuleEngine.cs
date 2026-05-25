using System.Collections.Concurrent;

namespace LTAI.DNA.Safety;

public sealed class SessionFact
{
    public string SessionId { get; init; } = "";
    public double RiskScore { get; init; }
    public int StrikeCount { get; init; }
    public int RequestCount { get; init; }
    public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
    public string? LastAction { get; init; }
}

public sealed class ModelFact
{
    public string ModelName { get; init; } = "";
    public double Temperature { get; init; }
    public int TokensUsed { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class KnowledgeFact
{
    public double PredictabilityIndex { get; init; }
    public int NodeCount { get; init; }
    public string? GraphType { get; init; }
}

public sealed class TimeWindowFact
{
    public string Key { get; init; } = "";
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; } = DateTime.UtcNow;
    public int EventCount { get; init; }
    public string? EventType { get; init; }
}

/// <summary>
/// Conflict resolution strategy when multiple rules fire simultaneously.
/// </summary>
public enum ConflictStrategy { Salience, Recency, Specificity, FirstMatch, AllMatch }

/// <summary>
/// Enhanced rule engine with temporal reasoning, multi-fact matching,
/// conflict resolution, rule governance, and auto-rule mining.
/// </summary>
public sealed class EnhancedRuleEngine
{
    private readonly List<EnhancedCompiledRule> _rules = new();
    private readonly ConcurrentDictionary<string, int> _hitCounts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _fireHistory = new();
    private readonly ConcurrentDictionary<string, EnhancedCompiledRule> _canaryRules = new();
    private readonly List<RuleAuditEntry> _auditLog = new();
    private readonly object _auditLock = new();
    private readonly Random _random = new();
    private ConflictStrategy _strategy = ConflictStrategy.AllMatch;

    public int RuleCount => _rules.Count;
    public IReadOnlyList<RuleAuditEntry> AuditLog { get { lock (_auditLock) return _auditLog.ToList(); } }

    public EnhancedRuleEngine WithStrategy(ConflictStrategy strategy) { _strategy = strategy; return this; }

    public void AddRule(string id, Action<EnhancedRuleBuilder> build)
    {
        var builder = new EnhancedRuleBuilder(id);
        build(builder);
        if (builder.IsValid)
            _rules.Add(builder.Compile());
    }

    /// <summary>
    /// Add a rule with canary deployment. Rule only fires for a percentage of traffic.
    /// </summary>
    public void AddCanaryRule(string id, int percentage, Action<EnhancedRuleBuilder> build)
    {
        var builder = new EnhancedRuleBuilder(id);
        build(builder);
        if (builder.IsValid)
        {
            var rule = builder.Compile();
            if (percentage >= 100) _rules.Add(rule);
            else _canaryRules[rule.Id] = rule;
        }
    }

    /// <summary>
    /// Add facts from multiple types. Rules that match ANY fact type will fire.
    /// </summary>
    public List<PolicyEvaluationResult> EvaluateMulti(object[] facts)
    {
        var allResults = new List<PolicyEvaluationResult>();
        foreach (var fact in facts)
        {
            var results = EvaluateSingle(fact);
            allResults.AddRange(results);
        }

        return _strategy switch
        {
            ConflictStrategy.FirstMatch => allResults.Take(1).ToList(),
            ConflictStrategy.Salience => allResults.OrderBy(r => r.Priority).ToList(),
            ConflictStrategy.Recency => allResults.OrderByDescending(r =>
                _fireHistory.GetValueOrDefault(r.RuleId)?.LastOrDefault() ?? DateTime.MinValue).ToList(),
            _ => allResults
        };
    }

    private List<PolicyEvaluationResult> EvaluateSingle(object fact)
    {
        var results = new List<PolicyEvaluationResult>();
        var factType = fact.GetType();

        foreach (var rule in GetActiveRules())
        {
            if (rule.CanaryPercent > 0 && _random.Next(100) >= rule.CanaryPercent)
                continue;

            if (!rule.MatchesFact(factType)) continue;
            if (!rule.Evaluate(fact)) continue;

            var ctx = new RuleContext(rule.Id);
            rule.Fire(ctx);

            _hitCounts.AddOrUpdate(rule.Id, 1, (_, c) => c + 1);
            _fireHistory.AddOrUpdate(rule.Id,
                _ => new List<DateTime> { DateTime.UtcNow },
                (_, l) => { l.Add(DateTime.UtcNow); return l; });

            LogAudit(rule, fact);

            results.AddRange(ctx.Hits.Select(h => new PolicyEvaluationResult
            {
                RuleId = h.RuleId,
                Triggered = h.Triggered,
                Action = h.Action,
                Message = h.Message,
                Priority = h.Priority
            }));
        }

        return results;
    }

    private IEnumerable<EnhancedCompiledRule> GetActiveRules()
    {
        foreach (var rule in _rules)
            yield return rule;
        foreach (var (_, rule) in _canaryRules)
            yield return rule;
    }

    /// <summary>
    /// Auto-mine rules from external instinct data.
    /// </summary>
    public void MineRulesFromInstincts(IEnumerable<(string Code, string Strategy, int SuccessCount)> instincts)
    {
        foreach (var (code, strategy, count) in instincts)
        {
            if (strategy.Contains("null-check") || strategy.Contains("conditional-access"))
            {
                AddRule($"auto_fix_{code}", b =>
                {
                    b.WithPriority(50)
                     .WithCategory("auto-mined")
                     .When<InputFact>(f => f.ContainsAny(code, $"error {code}"))
                     .Then(ctx => ctx.RecordHit(new RuleHit
                     {
                         RuleId = $"auto_fix_{code}",
                         Action = PolicyAction.Warn,
                         Triggered = true,
                         Message = $"Auto-detected: {strategy}. Fix applied {count}x successfully.",
                         Priority = 50
                     }));
                });
            }
        }
    }

    /// <summary>
    /// Generate knowledge graph health rules based on PI threshold.
    /// </summary>
    public void AddKnowledgeGraphRules(double piThreshold = 0.6)
    {
        AddRule("kg_low_pi_warning", b =>
        {
            b.WithPriority(200)
             .WithCategory("knowledge-graph")
             .When<KnowledgeFact>(f => f.PredictabilityIndex < piThreshold && f.NodeCount > 0)
             .Then(ctx =>
                 ctx.RecordHit(new RuleHit
                 {
                     RuleId = "kg_low_pi_warning",
                     Action = PolicyAction.Warn,
                     Triggered = true,
                     Message = $"Knowledge Graph PI is {piThreshold:F2} (below threshold). Consider adding more entities or rebuilding graph.",
                     Priority = 200
                 }));
        });
    }

    /// <summary>
    /// Add time-window rate limiting rules.
    /// Rule: if event count in window exceeds limit → block.
    /// </summary>
    public void AddRulesFromPolicy(PolicyAsCode policy)
    {
        foreach (var rule in policy.InputRules.Where(r => r.Enabled))
        {
            AddRule(rule.Id, b =>
            {
                b.WithPriority(rule.Priority).WithCategory("input")
                 .When<InputFact>(f =>
                     f.ContainsAny("ignore all previous", "system prompt", "DAN", "越狱",
                         "忽略之前", "无视规则", "rm -rf", "delete all", "drop database",
                         "<script", "javascript:", "eval(", "exec("))
                 .Then(ctx => ctx.RecordHit(new RuleHit
                 {
                     RuleId = rule.Id, Action = rule.Action,
                     Message = rule.Message, Triggered = true, Priority = rule.Priority
                 }));
            });
        }

        foreach (var rule in policy.OutputRules.Where(r => r.Enabled))
        {
            AddRule(rule.Id, b =>
            {
                b.WithPriority(rule.Priority).WithCategory("output")
                 .When<OutputFact>(f => f.Text.Length > 0)
                 .Then(ctx => ctx.RecordHit(new RuleHit
                 {
                     RuleId = rule.Id, Action = rule.Action,
                     Message = rule.Message, Triggered = true, Priority = rule.Priority
                 }));
            });
        }
    }

    public void AddTemporalRule(string id, string eventType, int maxEvents, TimeSpan window, PolicyAction action)
    {
        var history = new ConcurrentQueue<DateTime>();

        AddRule(id, b =>
        {
            b.WithPriority(20).WithCategory("temporal")
             .When<TimeWindowFact>(f =>
             {
                 if (f.EventType != eventType) return false;
                 history.Enqueue(f.EndTime);
                 while (history.TryPeek(out var t) && t < DateTime.UtcNow - window)
                     history.TryDequeue(out _);
                 return history.Count > maxEvents;
             })
             .Then(ctx =>
                 ctx.RecordHit(new RuleHit
                 {
                     RuleId = id,
                     Action = action,
                     Triggered = true,
                     Message = $"{eventType} rate limit exceeded: {history.Count} events in {window.TotalMinutes:F0}min (limit: {maxEvents})",
                     Priority = 20
                 }));
        });
    }

    private void LogAudit(EnhancedCompiledRule rule, object fact)
    {
        lock (_auditLock)
        {
            _auditLog.Add(new RuleAuditEntry
            {
                RuleId = rule.Id,
                FactType = fact.GetType().Name,
                FactSnapshot = fact.ToString() ?? "",
                Timestamp = DateTime.UtcNow,
                Category = rule.Category
            });

            if (_auditLog.Count > 10000)
                _auditLog.RemoveRange(0, 5000);
        }
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_rules"] = _rules.Count + _canaryRules.Count,
        ["canary_rules"] = _canaryRules.Count,
        ["hit_counts"] = _hitCounts.ToDictionary(k => k.Key, v => (object)v.Value),
        ["audit_log_size"] = _auditLog.Count,
        ["strategy"] = _strategy.ToString()
    };

    /// <summary>
    /// Auto-tune rule priorities: rules with high false positive rate → lower priority.
    /// Called periodically (e.g., from DreamCycle after consolidation).
    /// </summary>
    public int AutoTuneRulePriorities(Dictionary<string, int> falsePositives)
    {
        var tuned = 0;
        foreach (var rule in _rules.ToList())
        {
            if (falsePositives.TryGetValue(rule.Id, out var fp) && fp >= 3)
            {
                var newRule = new EnhancedCompiledRule(
                    rule.Id, rule.Priority + fp * 10, rule.Category, rule.CanaryPercent,
                    rule.ConditionType, rule.Condition, rule.FireAction);
                _rules.Remove(rule);
                _rules.Add(newRule);
                tuned++;
            }
        }
        return tuned;
    }

    /// <summary>
    /// Flag rules that haven't fired in N days for review.
    /// </summary>
    public List<string> DetectStaleRules(int staleDays = 30)
    {
        var stale = new List<string>();
        var cutoff = DateTime.UtcNow.AddDays(-staleDays);

        foreach (var rule in _rules.ToList())
        {
            var lastFire = _auditLog
                .Where(a => a.RuleId == rule.Id)
                .OrderByDescending(a => a.Timestamp)
                .FirstOrDefault();

            if (lastFire == null || lastFire.Timestamp < cutoff)
            {
                stale.Add(rule.Id);
            }
        }

        return stale;
    }

    /// <summary>
    /// Wire DreamCycle consolidation → auto-mine rules from FixInstinctStore.
    /// Called from DreamCycle after each consolidation cycle.
    /// </summary>
    public void BridgeFromDreamCycle(Dictionary<string, (string Strategy, int SuccessCount)> topInstincts)
    {
        foreach (var (code, (strategy, count)) in topInstincts)
        {
            AddCanaryRule($"dream_{code}", percentage: 10, b =>
            {
                b.WithPriority(60).WithCategory("dream-bridge")
                 .WithCanary(10)
                 .When<InputFact>(f => f.ContainsAny(code))
                 .Then(ctx => ctx.RecordHit(new RuleHit
                 {
                     RuleId = $"dream_{code}",
                     Action = PolicyAction.Warn,
                     Triggered = true,
                     Message = $"[DreamCycle] Learned fix pattern: {strategy} (success rate: {count}x)",
                     Priority = 60
                 }));
            });
        }
    }
}

public sealed class RuleAuditEntry
{
    public string RuleId { get; init; } = "";
    public string FactType { get; init; } = "";
    public string FactSnapshot { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
}

public sealed class EnhancedRuleBuilder
{
    private readonly string _id;
    private int _priority = 100;
    private string _category = "input";
    private Delegate? _condition;
    private Action<RuleContext>? _action;
    private int _canaryPercent;

    public EnhancedRuleBuilder(string id) => _id = id;
    public bool IsValid => _condition != null && _action != null;

    public EnhancedRuleBuilder WithPriority(int p) { _priority = p; return this; }
    public EnhancedRuleBuilder WithCategory(string c) { _category = c; return this; }
    public EnhancedRuleBuilder WithCanary(int pct) { _canaryPercent = pct; return this; }

    public EnhancedRuleBuilder When<T>(Func<T, bool> condition) where T : class
    { _condition = condition; return this; }

    public EnhancedRuleBuilder Then(Action<RuleContext> action)
    { _action = action; return this; }

    public EnhancedCompiledRule Compile()
    {
        return new EnhancedCompiledRule(_id, _priority, _category, _canaryPercent,
            _condition!.GetType().GetGenericArguments()[0], _condition, _action!);
    }
}

public sealed class EnhancedCompiledRule
{
    public string Id { get; }
    public int Priority { get; }
    public string Category { get; }
    public int CanaryPercent { get; }
    public Type ConditionType { get; }
    private readonly Delegate _condition;
    private readonly Action<RuleContext> _action;

    public EnhancedCompiledRule(string id, int priority, string category, int canaryPercent,
        Type conditionType, Delegate condition, Action<RuleContext> action)
    {
        Id = id; Priority = priority; Category = category; CanaryPercent = canaryPercent;
        ConditionType = conditionType; _condition = condition; _action = action;
    }

    public bool MatchesFact(Type factType) => ConditionType.IsAssignableFrom(factType);

    public bool Evaluate(object fact)
    {
        var method = _condition.GetType().GetMethod("Invoke");
        return method != null && (bool)method.Invoke(_condition, new[] { fact })!;
    }

    public void Fire(RuleContext ctx) => _action(ctx);

    internal Delegate Condition => _condition;
    internal Action<RuleContext> FireAction => _action;
}
