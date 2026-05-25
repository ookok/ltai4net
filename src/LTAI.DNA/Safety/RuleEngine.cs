using System.Collections.Concurrent;

namespace LTAI.DNA.Safety;

public sealed class PolicyEvaluationResult
{
    public string RuleId { get; init; } = "";
    public bool Triggered { get; init; }
    public PolicyAction Action { get; init; }
    public string? Message { get; init; }
    public int Priority { get; init; } = 100;
}

public sealed class InputFact
{
    public string Text { get; init; } = "";
    public string? SessionId { get; init; }
    public bool ContainsAny(params string[] keywords) =>
        keywords.Any(k => Text.Contains(k, StringComparison.OrdinalIgnoreCase));
}

public sealed class OutputFact
{
    public string Text { get; init; } = "";
}

public sealed class ToolCallFact
{
    public string ToolName { get; init; } = "";
    public string Input { get; init; } = "";
}

public sealed class RuleEngine
{
    private readonly List<CompiledRule> _rules = new();
    private readonly ConcurrentDictionary<string, int> _hitCounts = new();

    public int RuleCount => _rules.Count;

    public void AddRule(string id, Action<RuleBuilder> build)
    {
        var builder = new RuleBuilder(id);
        build(builder);
        if (builder.IsValid)
            _rules.Add(builder.Compile());
    }

    public void AddRulesFromPolicy(PolicyAsCode policy)
    {
        foreach (var rule in policy.InputRules.Where(r => r.Enabled))
        {
            AddRule(rule.Id, b =>
            {
                b.WithPriority(rule.Priority)
                 .WithCategory("input")
                 .When<InputFact>(f =>
                     f.ContainsAny("ignore all previous", "system prompt", "DAN", "越狱", "忽略之前", "无视规则",
                         "rm -rf", "delete all file", "drop database", "<script", "javascript:"))
                 .Then(ctx =>
                     ctx.RecordHit(new RuleHit
                     {
                         RuleId = rule.Id, Action = rule.Action, Message = rule.Message,
                         Triggered = true, Priority = rule.Priority
                     }));
            });
        }

        foreach (var rule in policy.OutputRules.Where(r => r.Enabled))
        {
            AddRule(rule.Id, b =>
            {
                b.WithPriority(rule.Priority)
                 .WithCategory("output")
                 .When<OutputFact>(f => f.Text.Length > 0)
                 .Then(ctx =>
                     ctx.RecordHit(new RuleHit
                     {
                         RuleId = rule.Id, Action = rule.Action, Message = rule.Message,
                         Triggered = true, Priority = rule.Priority
                     }));
            });
        }
    }

    public List<PolicyEvaluationResult> EvaluateInput(string text)
    {
        return Evaluate(new InputFact { Text = text });
    }

    public List<PolicyEvaluationResult> EvaluateOutput(string text)
    {
        return Evaluate(new OutputFact { Text = text });
    }

    public List<PolicyEvaluationResult> EvaluateToolCall(string toolName, string input)
    {
        return Evaluate(new ToolCallFact { ToolName = toolName, Input = input });
    }

    private List<PolicyEvaluationResult> Evaluate<T>(T fact) where T : class
    {
        var results = new List<PolicyEvaluationResult>();

        foreach (var rule in _rules.OrderBy(r => r.Priority))
        {
            if (!rule.ConditionType.IsAssignableFrom(typeof(T)))
                continue;

            var cond = rule.GetTypedCondition<T>();
            if (cond == null || !cond(fact)) continue;

            var ctx = new RuleContext(rule.Id);
            rule.Fire(ctx);

            foreach (var hit in ctx.Hits)
            {
                _hitCounts.AddOrUpdate(rule.Id, 1, (_, c) => c + 1);
                results.Add(new PolicyEvaluationResult
                {
                    RuleId = hit.RuleId,
                    Triggered = hit.Triggered,
                    Action = hit.Action,
                    Message = hit.Message,
                    Priority = hit.Priority
                });
            }
        }

        return results;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_rules"] = _rules.Count,
        ["hits"] = _hitCounts.ToDictionary(k => k.Key, v => (object)v.Value)
    };
}

public sealed class RuleBuilder
{
    private readonly string _id;
    private int _priority = 100;
    private string _category = "input";
    private Delegate? _condition;
    private Action<RuleContext>? _action;

    public RuleBuilder(string id) => _id = id;
    public bool IsValid => _condition != null && _action != null;

    public RuleBuilder WithPriority(int p) { _priority = p; return this; }
    public RuleBuilder WithCategory(string c) { _category = c; return this; }

    public RuleBuilder When<T>(Func<T, bool> condition) where T : class
    { _condition = condition; return this; }

    public RuleBuilder Then(Action<RuleContext> action)
    { _action = action; return this; }

    public CompiledRule Compile()
    {
        return new CompiledRule(_id, _priority, _category,
            _condition!.GetType().GetGenericArguments()[0], _condition, _action!);
    }
}

public sealed class CompiledRule
{
    public string Id { get; }
    public int Priority { get; }
    public string Category { get; }
    public Type ConditionType { get; }
    private readonly Delegate _condition;
    private readonly Action<RuleContext> _action;

    public CompiledRule(string id, int priority, string category,
        Type conditionType, Delegate condition, Action<RuleContext> action)
    {
        Id = id; Priority = priority; Category = category;
        ConditionType = conditionType; _condition = condition; _action = action;
    }

    public Func<T, bool>? GetTypedCondition<T>() where T : class
        => _condition as Func<T, bool>;

    public void Fire(RuleContext ctx) => _action(ctx);
}

public sealed class RuleContext
{
    public string RuleId { get; }
    public List<RuleHit> Hits { get; } = new();
    public RuleContext(string id) => RuleId = id;
    public void RecordHit(RuleHit hit) => Hits.Add(hit);
}

public sealed class RuleHit
{
    public string RuleId { get; init; } = "";
    public bool Triggered { get; init; }
    public PolicyAction Action { get; init; }
    public string? Message { get; init; }
    public int Priority { get; init; } = 100;
}
